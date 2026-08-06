param(
    [string]$Path = ".\Pages\Organisation\Tree.cshtml"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $Path)) {
    throw "File not found: $Path"
}

$content = Get-Content -LiteralPath $Path -Raw
$original = $content

# 1. Start all expandable nodes collapsed and use ASCII-safe source.
$content = [regex]::Replace(
    $content,
    '<button type="button" class="org-toggle" aria-label="[^"]*@node\.DisplayName" aria-expanded="true">.*?</button>',
    '<button type="button" class="org-toggle" aria-label="@T("org.expand") @node.DisplayName" aria-expanded="false">+</button>'
)

# 2. Hide every non-root row on initial render.
$content = $content.Replace(
    '<article class="org-node"',
    '<article class="org-node @(node.Depth == 0 ? "" : "org-hidden")"'
)

# 3. Never store the Unicode minus character literally in the file.
#    The JS escape avoids the mojibake seen as âˆ’ / broken wrapped glyphs.
$content = $content.Replace("button.textContent = '−';", "button.textContent = '\u2212';")
$content = $content.Replace("button.textContent = ""−"";", "button.textContent = '\u2212';")

# 4. When search is cleared, return to the collapsed initial state.
$oldClear = @'
        if (!term) {
            for (const node of nodes) node.classList.remove('org-hidden');
            return;
        }
'@

$newClear = @'
        if (!term) {
            for (const node of nodes) {
                const isRoot = node.dataset.depth === '0';
                node.classList.toggle('org-hidden', !isRoot);

                const button = node.querySelector('.org-toggle');
                if (button) {
                    button.textContent = '+';
                    button.setAttribute('aria-expanded', 'false');
                    button.setAttribute(
                        'aria-label',
                        `${expandLabel} ${node.querySelector('.org-name').textContent}`
                    );
                }
            }
            return;
        }
'@

if ($content.Contains($oldClear)) {
    $content = $content.Replace($oldClear, $newClear)
}
else {
    throw "Could not find the search-clear block. No file was written."
}

# 5. Ensure Expand all also uses the ASCII-safe JS escape.
$content = $content.Replace("button.textContent = '−';", "button.textContent = '\u2212';")

if ($content -eq $original) {
    throw "No changes were made."
}

Set-Content -LiteralPath $Path -Value $content -Encoding utf8

Write-Host "Updated: $Path"
Write-Host ""
Write-Host "Checks:"
Select-String -Path $Path -Pattern `
    'aria-expanded="false">\+</button>', `
    'node.Depth == 0', `
    "textContent = '\\u2212'", `
    "const isRoot = node.dataset.depth === '0'"
