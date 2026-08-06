param(
    [string]$Path = ".\Pages\Organisation\Tree.cshtml"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $Path)) {
    throw "File not found: $Path"
}

$content = Get-Content -LiteralPath $Path -Raw
$original = $content

# Hide all non-root nodes on initial render.
$content = [regex]::Replace(
    $content,
    '<article class="org-node(?:\s+@\([^"]*\))?"',
    '<article class="org-node @(node.Depth == 0 ? "" : "org-hidden")"',
    [System.Text.RegularExpressions.RegexOptions]::IgnoreCase
)

# Replace the initial expand/collapse button regardless of the current
# translated label or corrupted minus character.
$content = [regex]::Replace(
    $content,
    '<button\s+type="button"\s+class="org-toggle"\s+aria-label="[^"]*"\s+aria-expanded="(?:true|false)">.*?</button>',
    '<button type="button" class="org-toggle" aria-label="@T("org.expand") @node.DisplayName" aria-expanded="false">+</button>',
    [System.Text.RegularExpressions.RegexOptions]::Singleline
)

$newScripts = @'
@section Scripts {
<script>
(() => {
    const expandLabel = @Html.Raw(System.Text.Json.JsonSerializer.Serialize(T("org.expand")));
    const collapseLabel = @Html.Raw(System.Text.Json.JsonSerializer.Serialize(T("org.collapse")));
    const tree = document.getElementById('orgTree');
    if (!tree) return;

    const nodes = [...tree.querySelectorAll('.org-node')];
    const bySam = new Map(nodes.map(node => [node.dataset.sam, node]));
    const children = new Map();

    for (const node of nodes) {
        const parent = node.dataset.parent;
        if (!parent) continue;
        if (!children.has(parent)) children.set(parent, []);
        children.get(parent).push(node);
    }

    function setButtonState(node, expanded) {
        const button = node.querySelector('.org-toggle');
        if (!button) return;

        button.textContent = expanded ? '\u2212' : '+';
        button.setAttribute('aria-expanded', expanded ? 'true' : 'false');

        const name = node.querySelector('.org-name')?.textContent ?? '';
        button.setAttribute(
            'aria-label',
            `${expanded ? collapseLabel : expandLabel} ${name}`
        );
    }

    function descendants(sam) {
        const result = [];
        const stack = [...(children.get(sam) || [])];

        while (stack.length) {
            const node = stack.pop();
            result.push(node);
            stack.push(...(children.get(node.dataset.sam) || []));
        }

        return result;
    }

    function collapse(node) {
        for (const child of descendants(node.dataset.sam)) {
            child.classList.add('org-hidden');
            setButtonState(child, false);
        }

        setButtonState(node, false);
    }

    function expand(node) {
        for (const child of children.get(node.dataset.sam) || []) {
            child.classList.remove('org-hidden');

            const childButton = child.querySelector('.org-toggle');
            if (childButton?.getAttribute('aria-expanded') === 'true') {
                expand(child);
            }
        }

        setButtonState(node, true);
    }

    function collapseAll() {
        for (const node of nodes) {
            const isRoot = node.dataset.depth === '0';
            node.classList.toggle('org-hidden', !isRoot);
            setButtonState(node, false);
        }
    }

    function expandAll() {
        for (const node of nodes) {
            node.classList.remove('org-hidden');
            setButtonState(node, true);
        }
    }

    tree.addEventListener('click', event => {
        const button = event.target.closest('.org-toggle');
        if (!button) return;

        const node = button.closest('.org-node');
        const isExpanded = button.getAttribute('aria-expanded') === 'true';

        if (isExpanded) {
            collapse(node);
        } else {
            expand(node);
        }
    });

    document.getElementById('expandAll')?.addEventListener('click', expandAll);
    document.getElementById('collapseAll')?.addEventListener('click', collapseAll);

    document.getElementById('orgSearch')?.addEventListener('input', event => {
        const term = event.target.value.trim().toLowerCase();

        if (!term) {
            collapseAll();
            return;
        }

        const visible = new Set();

        for (const node of nodes) {
            if (!node.dataset.search.includes(term)) continue;

            visible.add(node.dataset.sam);

            let parent = node.dataset.parent;
            while (parent) {
                visible.add(parent);
                parent = bySam.get(parent)?.dataset.parent || '';
            }
        }

        for (const node of nodes) {
            node.classList.toggle(
                'org-hidden',
                !visible.has(node.dataset.sam)
            );
        }
    });

    collapseAll();
})();
</script>
}
'@

$scriptsPattern = '(?s)@section\s+Scripts\s*\{.*\}\s*$'

if (-not [regex]::IsMatch($content, $scriptsPattern)) {
    throw "Could not find the Razor Scripts section. No changes were written."
}

$content = [regex]::Replace(
    $content,
    $scriptsPattern,
    [System.Text.RegularExpressions.MatchEvaluator]{ param($m) $newScripts },
    1
)

if ($content -eq $original) {
    throw "No matching organisation markup was changed."
}

Set-Content -LiteralPath $Path -Value $content -Encoding utf8

Write-Host "Updated: $Path"
Write-Host ""
Write-Host "Verification:"
Select-String -Path $Path -Pattern `
    'node.Depth == 0', `
    'aria-expanded="false">\+</button>', `
    "button.textContent = expanded \? '\\u2212' : '\+'", `
    'collapseAll\(\);'
