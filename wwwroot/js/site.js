// Storefront behaviour. Deliberately dependency-free (no jQuery plugins, no CDN) so the app
// works fully offline.
(function () {
    'use strict';

    /**
     * Prevents a double submission from creating two orders or two cart mutations: once a form
     * has been submitted, its submit button is disabled and shows a pending label.
     */
    function guardSubmitButtons() {
        document.addEventListener('submit', function (event) {
            var form = event.target;
            if (!(form instanceof HTMLFormElement) || form.dataset.noGuard === 'true') {
                return;
            }

            // Let client-side validation veto the submit first.
            if (typeof form.checkValidity === 'function' && !form.checkValidity()) {
                return;
            }

            var button = form.querySelector('button[type="submit"], input[type="submit"]');
            if (!button || button.disabled) {
                return;
            }

            var pending = button.dataset.pendingLabel || 'Working…';

            // Defer so the button is not disabled before the browser serialises the form.
            window.setTimeout(function () {
                button.disabled = true;
                if (button.tagName === 'BUTTON') {
                    button.dataset.originalLabel = button.innerHTML;
                    button.innerHTML = pending;
                } else {
                    button.dataset.originalValue = button.value;
                    button.value = pending;
                }
            }, 0);
        });
    }

    /**
     * Quantity steppers. Markup contract:
     *   <button data-qty-step="-1" data-qty-target="#someInput">−</button>
     *   <input id="someInput" type="number" min="1">
     */
    function wireQuantitySteppers() {
        document.addEventListener('click', function (event) {
            var trigger = event.target.closest('[data-qty-step]');
            if (!trigger) {
                return;
            }

            var selector = trigger.getAttribute('data-qty-target');
            var input = selector ? document.querySelector(selector) : null;
            if (!(input instanceof HTMLInputElement)) {
                return;
            }

            event.preventDefault();

            var step = parseInt(trigger.getAttribute('data-qty-step') || '0', 10);
            var min = input.min ? parseInt(input.min, 10) : 1;
            var max = input.max ? parseInt(input.max, 10) : Number.MAX_SAFE_INTEGER;
            var current = parseInt(input.value || '1', 10);

            if (isNaN(current)) {
                current = min;
            }

            var next = Math.min(max, Math.max(min, current + step));
            if (next !== current) {
                input.value = String(next);
                input.dispatchEvent(new Event('change', { bubbles: true }));
            }
        });
    }

    /** Collapses flash banners after a few seconds so they do not pile up. */
    function autoDismissAlerts() {
        var alerts = document.querySelectorAll('.alert-dismissible');
        Array.prototype.forEach.call(alerts, function (alert) {
            window.setTimeout(function () {
                if (typeof bootstrap !== 'undefined' && bootstrap.Alert) {
                    bootstrap.Alert.getOrCreateInstance(alert).close();
                } else {
                    alert.remove();
                }
            }, 8000);
        });
    }

    /**
     * Header search type-ahead. The search form carries a data-suggest-url pointing at the JSON
     * endpoint; this renders the reply into a dropdown with keyboard support. Everything degrades
     * to a plain search box if the attribute is missing or a request fails.
     */
    function wireSearchSuggestions() {
        var input = document.getElementById('siteSearch');
        if (!(input instanceof HTMLInputElement)) {
            return;
        }

        var form = input.closest('form');
        var box = document.getElementById('searchSuggestions');
        var url = form ? form.getAttribute('data-suggest-url') : null;
        if (!form || !box || !url) {
            return;
        }

        var timer = null;
        var active = -1;

        function close() {
            box.hidden = true;
            box.innerHTML = '';
            active = -1;
            input.setAttribute('aria-expanded', 'false');
        }

        function highlight() {
            var rendered = box.querySelectorAll('.search-suggest-item');
            Array.prototype.forEach.call(rendered, function (element, index) {
                element.classList.toggle('is-active', index === active);
            });
        }

        function makeItem(href, name, meta) {
            var link = document.createElement('a');
            link.className = 'search-suggest-item';
            link.setAttribute('role', 'option');
            link.href = href;

            var label = document.createElement('span');
            label.className = 'search-suggest-name';
            // textContent, never innerHTML: suggestion text is data, not markup.
            label.textContent = name;
            link.appendChild(label);

            if (meta) {
                var price = document.createElement('span');
                price.className = 'search-suggest-price';
                price.textContent = meta;
                link.appendChild(price);
            }

            return link;
        }

        function heading(text) {
            var element = document.createElement('div');
            element.className = 'search-suggest-heading';
            element.textContent = text;
            return element;
        }

        function render(payload) {
            var products = (payload && payload.products) || [];
            var categories = (payload && payload.categories) || [];

            if (!products.length && !categories.length) {
                close();
                return;
            }

            box.innerHTML = '';

            if (products.length) {
                box.appendChild(heading('Products'));
                products.forEach(function (product) {
                    box.appendChild(makeItem('/product/' + encodeURIComponent(product.slug), product.name, product.price));
                });
            }

            if (categories.length) {
                box.appendChild(heading('Departments'));
                categories.forEach(function (category) {
                    box.appendChild(makeItem('/Catalog?category=' + encodeURIComponent(category.slug), category.name, ''));
                });
            }

            active = -1;
            box.hidden = false;
            input.setAttribute('aria-expanded', 'true');
        }

        function load() {
            var term = input.value.trim();
            if (term.length < 2) {
                close();
                return;
            }

            fetch(url + '?q=' + encodeURIComponent(term), { headers: { 'X-Requested-With': 'XMLHttpRequest' } })
                .then(function (response) { return response.ok ? response.json() : null; })
                .then(function (payload) {
                    // Drop a response that arrived after the box moved on.
                    if (!payload || input.value.trim() !== term) {
                        return;
                    }
                    render(payload);
                })
                .catch(function () { close(); });
        }

        input.addEventListener('input', function () {
            window.clearTimeout(timer);
            timer = window.setTimeout(load, 180);
        });

        input.addEventListener('keydown', function (event) {
            var links = box.hidden ? [] : box.querySelectorAll('.search-suggest-item');

            if (event.key === 'ArrowDown' && links.length) {
                event.preventDefault();
                active = (active + 1) % links.length;
                highlight();
                links[active].scrollIntoView({ block: 'nearest' });
            } else if (event.key === 'ArrowUp' && links.length) {
                event.preventDefault();
                active = active <= 0 ? links.length - 1 : active - 1;
                highlight();
                links[active].scrollIntoView({ block: 'nearest' });
            } else if (event.key === 'Enter' && active >= 0 && links[active]) {
                event.preventDefault();
                links[active].click();
            } else if (event.key === 'Escape') {
                close();
            }
        });

        // A short delay lets a click on a suggestion land before the menu is torn down.
        input.addEventListener('blur', function () {
            window.setTimeout(close, 150);
        });

        document.addEventListener('click', function (event) {
            if (!form.contains(event.target)) {
                close();
            }
        });
    }

    document.addEventListener('DOMContentLoaded', function () {
        guardSubmitButtons();
        wireQuantitySteppers();
        autoDismissAlerts();
        wireSearchSuggestions();
    });
})();
