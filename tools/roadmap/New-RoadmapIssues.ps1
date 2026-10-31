#Requires -Version 7.2
<#
.SYNOPSIS
    Creates the roadmap issues from docs/roadmap/issues.md on GitHub: labels, milestones, one
    issue per section with Depends on / Blocks links, native "blocked by" relationships, and a
    Roadmap tracking issue with a checklist.

.DESCRIPTION
    Needs the GitHub CLI logged in with write access (`gh auth login`). Safe to re-run: issues
    are matched by title and not created twice, and links and the Roadmap issue are refreshed.

.PARAMETER DryRun
    Parse the file and print what would be created, without calling GitHub.
#>
param([switch] $DryRun)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$source = Join-Path $root 'docs/roadmap/issues.md'

# ---------------------------------------------------------------- parse
$sections = [System.Collections.Generic.List[object]]::new()
$current = $null
$fenced = $false
foreach ($line in (Get-Content $source -Encoding utf8)) {
    # Only headings outside code blocks start a section (the file documents its own format).
    if ($line -match '^```') { $fenced = -not $fenced }
    if (-not $fenced -and $line -match '^## ([a-z0-9-]+): (.+)$') {
        $current = [ordered] @{ Key = $Matches[1]; Title = $Matches[2].Trim(); Milestone = ''; Labels = @(); Depends = @(); Body = [System.Text.StringBuilder]::new(); InHeader = $true }
        $sections.Add($current)
        continue
    }
    if (-not $current) { continue }
    if ($current.InHeader) {
        if ($line -match '^milestone:\s*(.+)$') { $current.Milestone = $Matches[1].Trim(); continue }
        if ($line -match '^labels:\s*(.+)$') { $current.Labels = @($Matches[1].Split(',') | ForEach-Object { $_.Trim() } | Where-Object { $_ }); continue }
        if ($line -match '^depends:\s*(.+)$') {
            $current.Depends = @(if ($Matches[1].Trim() -ne 'none') { $Matches[1].Split(',') | ForEach-Object { $_.Trim() } })
            continue
        }
        if ($line -eq '') { $current.InHeader = $false; continue }
    }
    [void]$current.Body.AppendLine($line)
}
$byKey = @{}
foreach ($s in $sections) {
    if ($byKey.ContainsKey($s.Key)) { throw "Duplicate key $($s.Key)" }
    $byKey[$s.Key] = $s
}
$order = @{}
for ($i = 0; $i -lt $sections.Count; $i++) { $order[$sections[$i].Key] = $i }
foreach ($s in $sections) {
    foreach ($d in $s.Depends) {
        if (-not $byKey.ContainsKey($d)) { throw "$($s.Key) depends on unknown key '$d'" }
        if ($order[$d] -ge $order[$s.Key]) { throw "$($s.Key) depends on '$d', which comes later in the file" }
    }
    if (-not $s.Milestone) { throw "$($s.Key) has no milestone" }
}
$blocks = @{}
foreach ($s in $sections) { foreach ($d in $s.Depends) { $blocks[$d] = @($blocks[$d]) + $s.Key | Where-Object { $_ } } }
"Parsed $($sections.Count) issues."

if ($DryRun) {
    foreach ($s in $sections) {
        "{0,-30} {1,-40} deps: {2}" -f $s.Key, $s.Milestone, ($(if ($s.Depends) { $s.Depends -join ', ' } else { '-' }))
    }
    return
}

# ---------------------------------------------------------------- GitHub helpers
$repo = (gh repo view --json nameWithOwner -q .nameWithOwner)
if ($LASTEXITCODE) { throw 'gh is not logged in or this is not a GitHub repo: run gh auth login.' }
"Repository: $repo"

function Invoke-Gh { $out = & gh @args; if ($LASTEXITCODE) { throw "gh $($args -join ' ') failed" }; $out }

$labelColours = @{
    'type: feature' = '1F6FEB'; 'type: spike' = '8B5CF6'; 'type: infra' = '64748B'; 'type: docs' = '14B8A6'; 'type: bug' = 'D73A4A'
    'P1' = 'B60205'; 'P2' = 'FBCA04'; 'P3' = 'C2E0C6'
    'needs-azure' = '0078D4'; 'needs-hyperv-vm' = '5C2D91'; 'needs-maintainer' = 'E99695'
    'agent-ready' = '2E9E5B'; 'blocked' = '000000'; 'roadmap' = '0E8A16'
}
$allLabels = @($sections | ForEach-Object { $_.Labels }) + @('agent-ready', 'blocked', 'roadmap') | Sort-Object -Unique
foreach ($label in $allLabels) {
    $colour = if ($labelColours.ContainsKey($label)) { $labelColours[$label] } elseif ($label -like 'area:*') { 'BFD4F2' } else { 'EDEDED' }
    Invoke-Gh label create $label --color $colour --force | Out-Null
}
"Labels ready."

$milestones = @{}
foreach ($m in (Invoke-Gh api "repos/$repo/milestones?state=all&per_page=100" | ConvertFrom-Json)) { $milestones[$m.title] = $m.number }
foreach ($title in ($sections | ForEach-Object Milestone | Select-Object -Unique)) {
    if (-not $milestones.ContainsKey($title)) {
        $created = Invoke-Gh api "repos/$repo/milestones" -f "title=$title" | ConvertFrom-Json
        $milestones[$title] = $created.number
    }
}
"Milestones ready."

$existing = @{}
foreach ($i in (Invoke-Gh issue list --state all --limit 500 --json number,title | ConvertFrom-Json)) { $existing[$i.title] = $i.number }

function Format-Body($s, $numbers) {
    $body = $s.Body.ToString().Trim()
    $body = [regex]::Replace($body, '\{\{([a-z0-9-]+)\}\}', { param($m) if ($numbers.ContainsKey($m.Groups[1].Value)) { "#$($numbers[$m.Groups[1].Value])" } else { $m.Value } })
    $deps = @($s.Depends | ForEach-Object { if ($numbers.ContainsKey($_)) { "#$($numbers[$_])" } })
    $blockedOn = @($blocks[$s.Key] | Where-Object { $_ } | ForEach-Object { if ($numbers.ContainsKey($_)) { "#$($numbers[$_])" } })
    $links = @('---', "**Depends on:** $(if ($deps) { $deps -join ', ' } else { 'nothing; can start now' })")
    if ($blockedOn) { $links += "**Blocks:** $($blockedOn -join ', ')" }
    $links += "_Roadmap key: ``$($s.Key)``. Agents: see [AGENTS.md](../blob/main/AGENTS.md) for how to pick up and finish an issue._"
    "$body`n`n$($links -join "`n`n")"
}

# ---------------------------------------------------------------- issues, in dependency order
$numbers = @{}
$tmp = New-TemporaryFile
try {
    foreach ($s in $sections) {
        if ($existing.ContainsKey($s.Title)) {
            $numbers[$s.Key] = $existing[$s.Title]
            "  exists  #$($numbers[$s.Key])  $($s.Title)"
            continue
        }
        Set-Content $tmp (Format-Body $s $numbers) -Encoding utf8
        $labels = @($s.Labels) + @(if ($s.Depends) { 'blocked' } elseif ($s.Labels -notcontains 'needs-maintainer') { 'agent-ready' })
        $url = Invoke-Gh issue create --title $s.Title --body-file $tmp --milestone $s.Milestone --label ($labels -join ',')
        $numbers[$s.Key] = [int]($url.Trim() -replace '.*/', '')
        "  created #$($numbers[$s.Key])  $($s.Title)"
    }

    # Second pass: every link resolved (including forward references), Blocks lines added.
    foreach ($s in $sections) {
        Set-Content $tmp (Format-Body $s $numbers) -Encoding utf8
        Invoke-Gh issue edit $numbers[$s.Key] --body-file $tmp | Out-Null
    }
    "Links updated."

    # Native "blocked by" relationships (GitHub issue dependencies), best effort.
    foreach ($s in $sections) {
        foreach ($d in $s.Depends) {
            $id = (Invoke-Gh api "repos/$repo/issues/$($numbers[$d])" | ConvertFrom-Json).id
            & gh api -X POST "repos/$repo/issues/$($numbers[$s.Key])/dependencies/blocked_by" -F "issue_id=$id" 2>$null | Out-Null
        }
    }
    "Dependencies linked."

    # Roadmap tracking issue: the to-do list, grouped by milestone.
    $lines = @('The project to-do list. Each item is an issue and shows whether it is open or closed; tick it when it closes.', '',
        'Agents: take the lowest-numbered open issue labelled `agent-ready` whose dependencies are all closed. See [AGENTS.md](../blob/main/AGENTS.md).', '')
    foreach ($milestone in ($sections | ForEach-Object Milestone | Select-Object -Unique)) {
        $lines += "### $milestone"
        foreach ($s in $sections | Where-Object Milestone -eq $milestone) {
            $after = if ($s.Depends) { ' (after ' + (($s.Depends | ForEach-Object { "#$($numbers[$_])" }) -join ', ') + ')' } else { '' }
            $lines += "- [ ] #$($numbers[$s.Key])$after"
        }
        $lines += ''
    }
    Set-Content $tmp ($lines -join "`n") -Encoding utf8
    $title = 'Roadmap: what is left after v0.1.0'
    if ($existing.ContainsKey($title)) {
        Invoke-Gh issue edit $existing[$title] --body-file $tmp | Out-Null
        "Roadmap issue #$($existing[$title]) updated."
    }
    else {
        $url = Invoke-Gh issue create --title $title --body-file $tmp --label roadmap
        Invoke-Gh issue pin ($url.Trim() -replace '.*/', '') | Out-Null
        "Roadmap issue created and pinned: $url"
    }
}
finally {
    Remove-Item $tmp -ErrorAction SilentlyContinue
}
