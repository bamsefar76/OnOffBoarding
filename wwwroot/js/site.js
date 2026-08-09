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
