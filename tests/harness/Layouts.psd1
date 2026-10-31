# Disk layout catalogue for the harness. Sizes are in MiB.
#
# SectorFormat: 512n (512 logical / 512 physical), 512e (512 / 4096) or 4Kn (4096 / 4096).
# Partitions are created in order. Offset (MiB) is optional; without it a partition follows
# the previous one. Size 0 means "use the rest of the disk". FillPercent controls how much
# deterministic test data is written to the volume. On MBR, Kind = 'Extended' makes an
# extended partition (no file system) and Kind = 'Logical' a logical drive inside the extended
# partition before it; Windows puts 64 KiB of chain overhead before each logical drive.
# Keep at least ~40 MiB free on small volumes: Repair-Volume -Scan needs room for a snapshot.
#
# Kept small on purpose: every integration run builds, fills, moves and re-reads these, and
# the shapes (overlap, alignment, sector size, table style) matter, not the size. GPT disks
# also get a 16 MiB MSR from Initialize-Disk, so the first GPT partition starts at 16 MiB.
@{
    'gpt-512n-single' = @{
        Description  = 'One data partition with free space after it.'
        Style        = 'GPT'
        SectorFormat = '512n'
        DiskSizeMB   = 1024
        Partitions   = @(
            @{ SizeMB = 512; FileSystem = 'NTFS'; Label = 'DATA1'; FillPercent = 60 }
        )
    }

    'gpt-512e-boxed-in' = @{
        Description  = 'The case that started the project: the middle partition needs to grow, free space is at the end.'
        Style        = 'GPT'
        SectorFormat = '512e'
        DiskSizeMB   = 2048
        Partitions   = @(
            @{ SizeMB = 384; FileSystem = 'NTFS'; Label = 'LEFT';   FillPercent = 50 }
            @{ SizeMB = 384; FileSystem = 'NTFS'; Label = 'TARGET'; FillPercent = 80 }
            @{ SizeMB = 256; FileSystem = 'NTFS'; Label = 'RIGHT';  FillPercent = 50 }
        )
    }

    'gpt-512n-free-left' = @{
        Description  = 'Free space before the partition, for left moves that overlap their source.'
        Style        = 'GPT'
        SectorFormat = '512n'
        DiskSizeMB   = 1024
        Partitions   = @(
            @{ OffsetMB = 273; SizeMB = 512; FileSystem = 'NTFS'; Label = 'SHIFTME'; FillPercent = 70 }
        )
    }

    'gpt-4kn-three' = @{
        Description  = 'Three partitions on a 4K native disk.'
        Style        = 'GPT'
        SectorFormat = '4Kn'
        DiskSizeMB   = 2048
        Partitions   = @(
            @{ SizeMB = 256; FileSystem = 'NTFS'; Label = 'A'; FillPercent = 40 }
            @{ SizeMB = 256; FileSystem = 'NTFS'; Label = 'B'; FillPercent = 90 }
            @{ SizeMB = 256; FileSystem = 'NTFS'; Label = 'C'; FillPercent = 10 }
        )
    }

    'gpt-512n-tight' = @{
        Description  = 'About 49 MiB free after the partition and nothing else: a move right uses nearly all of it, so almost no sectors lie outside the move ranges (plan gap G5).'
        Style        = 'GPT'
        SectorFormat = '512n'
        DiskSizeMB   = 578
        Partitions   = @(
            @{ SizeMB = 512; FileSystem = 'NTFS'; Label = 'FULL'; FillPercent = 60 }
        )
    }

    'mbr-512n-single' = @{
        Description  = 'MBR equivalent of gpt-512n-single (MountedDevices uses signature + offset, plan gap G1).'
        Style        = 'MBR'
        SectorFormat = '512n'
        DiskSizeMB   = 1024
        Partitions   = @(
            @{ SizeMB = 512; FileSystem = 'NTFS'; Label = 'DATA1'; FillPercent = 60 }
        )
    }

    'mbr-512e-boxed-in' = @{
        Description  = 'MBR equivalent of gpt-512e-boxed-in.'
        Style        = 'MBR'
        SectorFormat = '512e'
        DiskSizeMB   = 2048
        Partitions   = @(
            @{ SizeMB = 384; FileSystem = 'NTFS'; Label = 'LEFT';   FillPercent = 50 }
            @{ SizeMB = 384; FileSystem = 'NTFS'; Label = 'TARGET'; FillPercent = 80 }
            @{ SizeMB = 256; FileSystem = 'NTFS'; Label = 'RIGHT';  FillPercent = 50 }
        )
    }

    'mbr-512n-logical-boxed-in' = @{
        Description  = 'The Azure field case in miniature: SYS is boxed in by a primary and an extended partition holding three logical drives, with the free space at the end of the disk.'
        Style        = 'MBR'
        SectorFormat = '512n'
        DiskSizeMB   = 1024
        Partitions   = @(
            @{ SizeMB = 160; FileSystem = 'NTFS'; Label = 'SYS';  FillPercent = 50 }
            @{ SizeMB = 96;  FileSystem = 'NTFS'; Label = 'PRIM'; FillPercent = 40 }
            @{ Kind = 'Extended'; SizeMB = 320 }
            @{ Kind = 'Logical'; SizeMB = 96; FileSystem = 'NTFS'; Label = 'LOG1'; FillPercent = 45 }
            @{ Kind = 'Logical'; SizeMB = 96; FileSystem = 'NTFS'; Label = 'LOG2'; FillPercent = 40 }
            @{ Kind = 'Logical'; SizeMB = 96; FileSystem = 'NTFS'; Label = 'LOG3'; FillPercent = 45 }
        )
    }
}
