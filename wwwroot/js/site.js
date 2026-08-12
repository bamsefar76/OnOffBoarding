(() => {
    "use strict";

    const storageKey = "onoffboarding-theme";
    const root = document.documentElement;

    function getTheme() {
        const current = root.getAttribute("data-bs-theme");
        if (current === "dark" || current === "light") return current;

        const saved = localStorage.getItem(storageKey);
        if (saved === "dark" || saved === "light") return saved;

        return window.matchMedia("(prefers-color-scheme: dark)").matches
            ? "dark"
            : "light";
    }

    function applyTheme(theme) {
        root.setAttribute("data-bs-theme", theme);
        localStorage.setItem(storageKey, theme);

        const icon = document.getElementById("themeToggleIcon");
        const text = document.getElementById("themeToggleText");
        const button = document.getElementById("themeToggle");

        const dark = theme === "dark";
        if (icon) icon.textContent = dark ? "☀" : "☾";
        if (text && button) {
            text.textContent = dark
                ? button.dataset.lightText
                : button.dataset.darkText;
        }
        if (button) {
            const label = dark
                ? button.dataset.switchLightLabel
                : button.dataset.switchDarkLabel;
            if (label) {
                button.setAttribute("aria-label", label);
                button.setAttribute("title", label);
            }
        }
    }

    function initialiseThemeToggle() {
        applyTheme(getTheme());

        const button = document.getElementById("themeToggle");
        if (!button) return;

        button.addEventListener("click", () => {
            applyTheme(getTheme() === "dark" ? "light" : "dark");
        });
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", initialiseThemeToggle, { once: true });
    } else {
        initialiseThemeToggle();
    }
})();

(() => {
    "use strict";

    const collator = new Intl.Collator(document.documentElement.lang || undefined, {
        numeric: true,
        sensitivity: "base"
    });

    function normaliseText(value) {
        return (value ?? "")
            .replace(/\u00a0/g, " ")
            .replace(/\s+/g, " ")
            .trim();
    }

    function getCellText(cell) {
        if (cell.dataset.sortValue !== undefined) {
            return normaliseText(cell.dataset.sortValue);
        }

        const control = Array.from(cell.querySelectorAll("input, select, textarea"))
            .find(element => element.type !== "hidden");

        if (control) {
            if (control instanceof HTMLInputElement && control.type === "checkbox") {
                return control.checked ? "1" : "0";
            }

            if (control instanceof HTMLSelectElement) {
                return normaliseText(control.selectedOptions[0]?.textContent ?? control.value);
            }

            return normaliseText(control.value);
        }

        return normaliseText(cell.innerText || cell.textContent || "");
    }

    function parseNorwegianDate(text) {
        const match = text.match(/^(\d{1,2})\.(\d{1,2})\.(\d{4})(?:\s+(\d{1,2}):(\d{2})(?::(\d{2}))?)?(?:\s+.*)?$/);
        if (!match) return null;

        const day = Number(match[1]);
        const month = Number(match[2]);
        const year = Number(match[3]);
        const hour = Number(match[4] || 0);
        const minute = Number(match[5] || 0);
        const second = Number(match[6] || 0);

        const value = new Date(year, month - 1, day, hour, minute, second).getTime();
        return Number.isNaN(value) ? null : value;
    }

    function parseIsoDate(text) {
        const match = text.match(/^(\d{4})-(\d{2})-(\d{2})(?:[ T](\d{1,2}):(\d{2})(?::(\d{2}))?)?(?:\s+.*)?$/);
        if (!match) return null;

        const year = Number(match[1]);
        const month = Number(match[2]);
        const day = Number(match[3]);
        const hour = Number(match[4] || 0);
        const minute = Number(match[5] || 0);
        const second = Number(match[6] || 0);

        const value = new Date(year, month - 1, day, hour, minute, second).getTime();
        return Number.isNaN(value) ? null : value;
    }

    function parseNumber(text) {
        const candidate = text.replace(/^#\s*/, "").replace(/\s/g, "");
        if (!/^[+-]?\d+(?:[.,]\d+)?$/.test(candidate)) return null;

        const value = Number(candidate.replace(",", "."));
        return Number.isNaN(value) ? null : value;
    }

    function makeSortValue(text, explicitType) {
        if (text === "" || text === "-" || text === "—") return { type: "empty", value: "" };
        if (explicitType === "text") return { type: "text", value: text };
        if (explicitType === "number") return { type: "number", value: parseNumber(text) ?? Number.NEGATIVE_INFINITY };
        if (explicitType === "date") return { type: "date", value: parseNorwegianDate(text) ?? parseIsoDate(text) ?? Number.NEGATIVE_INFINITY };

        const date = parseNorwegianDate(text) ?? parseIsoDate(text);
        if (date !== null) return { type: "date", value: date };

        const number = parseNumber(text);
        if (number !== null) return { type: "number", value: number };

        return { type: "text", value: text };
    }

    function getSortValue(row, columnIndex, explicitType) {
        const rowSortValue = row.getAttribute(`data-sort-column-${columnIndex}`);
        if (rowSortValue !== null) {
            return makeSortValue(normaliseText(rowSortValue), explicitType);
        }

        const cell = row.cells[columnIndex];
        if (!cell) return null;

        return makeSortValue(getCellText(cell), explicitType);
    }

    function compareValues(left, right) {
        if (left.type === right.type && (left.type === "number" || left.type === "date")) {
            return left.value - right.value;
        }

        return collator.compare(String(left.value), String(right.value));
    }

    function sortTable(table, header, columnIndex) {
        const direction = header.getAttribute("aria-sort") === "ascending" ? "descending" : "ascending";
        const multiplier = direction === "ascending" ? 1 : -1;
        const explicitType = header.dataset.sortType || "auto";

        table.querySelectorAll("thead th[aria-sort]").forEach(otherHeader => {
            if (otherHeader !== header) otherHeader.setAttribute("aria-sort", "none");
        });
        header.setAttribute("aria-sort", direction);

        Array.from(table.tBodies).forEach(tbody => {
            const rows = Array.from(tbody.rows);
            const sortableRows = [];
            const fixedRows = [];

            rows.forEach((row, originalIndex) => {
                const hasRowSortValue = row.hasAttribute(`data-sort-column-${columnIndex}`);
                const hasSpanningCell = Array.from(row.cells).some(rowCell => rowCell.colSpan > 1 || rowCell.rowSpan > 1);
                const sortValue = getSortValue(row, columnIndex, explicitType);

                if (!sortValue || (hasSpanningCell && !hasRowSortValue)) {
                    fixedRows.push({ row, originalIndex });
                    return;
                }

                sortableRows.push({
                    row,
                    originalIndex,
                    sortValue
                });
            });

            sortableRows.sort((left, right) => {
                if (left.sortValue.type === "empty" && right.sortValue.type !== "empty") return 1;
                if (right.sortValue.type === "empty" && left.sortValue.type !== "empty") return -1;

                const comparison = compareValues(left.sortValue, right.sortValue);
                return comparison === 0
                    ? left.originalIndex - right.originalIndex
                    : comparison * multiplier;
            });

            const fragment = document.createDocumentFragment();
            sortableRows.forEach(item => fragment.appendChild(item.row));
            fixedRows
                .sort((left, right) => left.originalIndex - right.originalIndex)
                .forEach(item => fragment.appendChild(item.row));
            tbody.appendChild(fragment);
        });
    }

    function initialiseSortableTable(table) {
        if (!(table instanceof HTMLTableElement)) return;
        if (table.dataset.sortable === "false" || table.dataset.sortInitialised === "true") return;
        if (!table.tHead || table.tBodies.length === 0) return;

        const headerRow = table.tHead.rows[table.tHead.rows.length - 1];
        if (!headerRow) return;

        let sortableColumnCount = 0;

        Array.from(headerRow.cells).forEach((header, columnIndex) => {
            if (!(header instanceof HTMLTableCellElement)) return;
            if (header.dataset.sortable === "false" || header.colSpan > 1) return;

            const headerText = normaliseText(header.innerText || header.textContent || "");
            if (!headerText) return;

            const button = document.createElement("button");
            button.type = "button";
            button.className = "app-table-sort-button";
            button.setAttribute("aria-label", headerText);

            while (header.firstChild) {
                button.appendChild(header.firstChild);
            }

            header.appendChild(button);
            header.setAttribute("aria-sort", "none");
            header.dataset.sortableColumn = "true";
            button.addEventListener("click", () => sortTable(table, header, columnIndex));
            sortableColumnCount += 1;
        });

        if (sortableColumnCount > 0) {
            table.classList.add("app-sortable-table");
            table.dataset.sortInitialised = "true";
        }
    }

    function initialiseSortableTables(root = document) {
        if (root instanceof HTMLTableElement) initialiseSortableTable(root);
        root.querySelectorAll?.("table").forEach(initialiseSortableTable);
    }

    function startSortableTables() {
        document.querySelectorAll('details[data-auto-open="true"]').forEach(details => {
            details.open = true;
        });

        initialiseSortableTables();

        const observer = new MutationObserver(mutations => {
            for (const mutation of mutations) {
                for (const node of mutation.addedNodes) {
                    if (!(node instanceof Element)) continue;
                    initialiseSortableTables(node);
                }
            }
        });

        observer.observe(document.body, { childList: true, subtree: true });
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", startSortableTables, { once: true });
    } else {
        startSortableTables();
    }
})();
