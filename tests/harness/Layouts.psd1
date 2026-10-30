# Disk layout catalogue for the harness. Sizes are in MiB.
#
# SectorFormat: 512n (512 logical / 512 physical), 512e (512 / 4096) or 4Kn (4096 / 4096).
# Partitions are created in order. Offset (MiB) is optional; without it a partition follows
# the previous one. Size 0 means "use the rest of the disk". FillPercent controls how much
# deterministic test data is written to the volume.
@{
    'gpt-512n-single' = @{
        Description  = 'One data partition with free space after it.'
        Style        = 'GPT'
        SectorFormat = '512n'
        DiskSizeMB   = 2048
        Partitions   = @(
            @{ SizeMB = 1024; FileSystem = 'NTFS'; Label = 'DATA1'; FillPercent = 60 }
        )
    }

    'gpt-512e-boxed-in' = @{
        Description  = 'The case that started the project: the middle partition needs to grow, free space is at the end.'
        Style        = 'GPT'
        SectorFormat = '512e'
        DiskSizeMB   = 4096
        Partitions   = @(
            @{ SizeMB = 768;  FileSystem = 'NTFS'; Label = 'LEFT';   FillPercent = 50 }
            @{ SizeMB = 768;  FileSystem = 'NTFS'; Label = 'TARGET'; FillPercent = 80 }
            @{ SizeMB = 512;  FileSystem = 'NTFS'; Label = 'RIGHT';  FillPercent = 50 }
        )
    }

    'gpt-512n-free-left' = @{
        Description  = 'Free space before the partition, for left moves.'
        Style        = 'GPT'
        SectorFormat = '512n'
        DiskSizeMB   = 2048
        Partitions   = @(
            @{ OffsetMB = 513; SizeMB = 1024; FileSystem = 'NTFS'; Label = 'SHIFTME'; FillPercent = 70 }
        )
    }

    'gpt-4kn-three' = @{
        Description  = 'Three partitions on a 4K native disk.'
        Style        = 'GPT'
        SectorFormat = '4Kn'
        DiskSizeMB   = 4096
        Partitions   = @(
            @{ SizeMB = 512; FileSystem = 'NTFS'; Label = 'A'; FillPercent = 40 }
            @{ SizeMB = 512; FileSystem = 'NTFS'; Label = 'B'; FillPercent = 90 }
            @{ SizeMB = 512; FileSystem = 'NTFS'; Label = 'C'; FillPercent = 10 }
        )
    }

    'gpt-512n-tight' = @{
        Description  = 'About 49 MiB free after the partition and nothing else: a move right uses all of it, so no sectors lie outside the move ranges (plan gap G5).'
        Style        = 'GPT'
        SectorFormat = '512n'
        DiskSizeMB   = 1090
        Partitions   = @(
            @{ SizeMB = 1024; FileSystem = 'NTFS'; Label = 'FULL'; FillPercent = 60 }
        )
    }

    'mbr-512n-single' = @{
        Description  = 'MBR equivalent of gpt-512n-single (MountedDevices uses signature + offset, plan gap G1).'
        Style        = 'MBR'
        SectorFormat = '512n'
        DiskSizeMB   = 2048
        Partitions   = @(
            @{ SizeMB = 1024; FileSystem = 'NTFS'; Label = 'DATA1'; FillPercent = 60 }
        )
    }

    'mbr-512e-boxed-in' = @{
        Description  = 'MBR equivalent of gpt-512e-boxed-in.'
        Style        = 'MBR'
        SectorFormat = '512e'
        DiskSizeMB   = 4096
        Partitions   = @(
            @{ SizeMB = 768; FileSystem = 'NTFS'; Label = 'LEFT';   FillPercent = 50 }
            @{ SizeMB = 768; FileSystem = 'NTFS'; Label = 'TARGET'; FillPercent = 80 }
            @{ SizeMB = 512; FileSystem = 'NTFS'; Label = 'RIGHT';  FillPercent = 50 }
        )
    }
}
