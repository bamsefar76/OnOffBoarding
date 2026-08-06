param(
    [string]$Path = ".\Pages\Organisation\Tree.cshtml"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $Path)) {
    throw "File not found: $Path"
}

$content = Get-Content -LiteralPath $Path -Raw

$oldCss = @'
    .org-tree {
        display: grid;
        gap: .45rem;
    }
    .org-node {
        --depth: 0;
        margin-left: calc(var(--depth) * 1.65rem);
        border: 1px solid #dee2e6;
        border-radius: .65rem;
        background: #fff;
        padding: .7rem .85rem;
        display: grid;
        grid-template-columns: auto minmax(0, 1fr) auto;
        align-items: center;
        gap: .7rem;
    }

    .org-node[data-depth="0"] {
        border-color: #adb5bd;
        box-shadow: 0 .15rem .4rem rgba(0, 0, 0, .06);
    }
    .org-toggle {
        width: 2rem;
        height: 2rem;
        border: 0;
        border-radius: 50%;
        background: #eef1f4;
        font-weight: 700;
        line-height: 1;
    }

    .org-toggle-placeholder {
        width: 2rem;
        display: inline-block;
    }

    .org-name {
        font-weight: 650;
        font-size: 1.02rem;
    }

    .org-meta {
        color: #6c757d;
        font-size: .9rem;
        overflow-wrap: anywhere;
    }
    .org-actions {
        display: flex;
        gap: .45rem;
        align-items: center;
        flex-wrap: wrap;
        justify-content: flex-end;
    }

    .org-count {
        white-space: nowrap;
    }

    .org-hidden {
        display: none !important;
    }

    @@media (max-width: 767px) {
        .org-node {
            margin-left: calc(var(--depth) * .75rem);
            grid-template-columns: auto minmax(0, 1fr);
        }
        .org-actions {
            grid-column: 2;
            justify-content: flex-start;
        }
    }
'@

$newCss = @'
    .org-tree {
        display: grid;
        gap: .6rem;
    }

    .org-node {
        --depth: 0;
        position: relative;
        margin-left: calc(var(--depth) * 1.5rem);
        border: 1px solid var(--bs-border-color);
        border-radius: .75rem;
        background: var(--bs-tertiary-bg);
        color: var(--bs-body-color);
        padding: .8rem .95rem;
        display: grid;
        grid-template-columns: auto minmax(0, 1fr) auto;
        align-items: center;
        gap: .7rem;
        box-shadow: 0 .1rem .3rem rgba(0, 0, 0, .08);
        transition:
            border-color .15s ease,
            background-color .15s ease,
            box-shadow .15s ease;
    }

    .org-node[data-depth="0"] {
        background: var(--bs-secondary-bg);
        border-color: rgba(var(--bs-primary-rgb), .5);
        box-shadow: 0 .2rem .55rem rgba(0, 0, 0, .12);
    }

    .org-node:hover {
        border-color: rgba(var(--bs-primary-rgb), .65);
        box-shadow: 0 .25rem .65rem rgba(0, 0, 0, .12);
    }

    .org-node[data-depth]:not([data-depth="0"])::before {
        content: "";
        position: absolute;
        left: -.85rem;
        top: 50%;
        width: .7rem;
        border-top: 1px solid var(--bs-border-color);
    }

    .org-toggle {
        width: 2rem;
        height: 2rem;
        border: 1px solid var(--bs-border-color);
        border-radius: 50%;
        background: var(--bs-secondary-bg);
        color: var(--bs-body-color);
        font-weight: 700;
        line-height: 1;
        display: inline-flex;
        align-items: center;
        justify-content: center;
    }

    .org-toggle:hover,
    .org-toggle:focus-visible {
        border-color: var(--bs-primary);
        background: rgba(var(--bs-primary-rgb), .16);
        color: var(--bs-emphasis-color);
    }

    .org-toggle-placeholder {
        width: 2rem;
        display: inline-block;
    }

    .org-name {
        font-weight: 650;
        font-size: 1.02rem;
        color: var(--bs-emphasis-color);
    }

    .org-meta {
        color: var(--bs-secondary-color);
        font-size: .9rem;
        overflow-wrap: anywhere;
    }

    .org-actions {
        display: flex;
        gap: .45rem;
        align-items: center;
        flex-wrap: wrap;
        justify-content: flex-end;
    }

    .org-count {
        white-space: nowrap;
        color: var(--bs-body-color) !important;
        background: var(--bs-secondary-bg) !important;
        border: 1px solid var(--bs-border-color);
    }

    .org-hidden {
        display: none !important;
    }

    @@media (max-width: 767px) {
        .org-node {
            margin-left: calc(var(--depth) * .65rem);
            grid-template-columns: auto minmax(0, 1fr);
        }

        .org-node[data-depth]:not([data-depth="0"])::before {
            left: -.45rem;
            width: .35rem;
        }

        .org-actions {
            grid-column: 2;
            justify-content: flex-start;
        }
    }
'@

if (-not $content.Contains($oldCss)) {
    throw "The expected Organisation CSS block was not found. No changes were made."
}

$content = $content.Replace($oldCss, $newCss)
$content = $content.Replace('<div class="text-muted">', '<div class="text-body-secondary">')

Set-Content -LiteralPath $Path -Value $content -Encoding utf8

Write-Host "Updated: $Path"
Write-Host "Verify with:"
Write-Host "  Select-String -Path '$Path' -Pattern 'background: var\(--bs-tertiary-bg\)'"
