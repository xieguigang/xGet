/* =====================================================================
   docs.js — api reference document interactions
   + namespace tree filter (recursive match + auto expand)
   + index page namespace filter
   + keep the active namespace / type visible in the sidebar
   + smooth jump for the in page anchors
   ===================================================================== */

(function () {
    'use strict';

    function onReady(fn) {
        if (document.readyState !== 'loading') {
            fn();
        } else {
            document.addEventListener('DOMContentLoaded', fn);
        }
    }

    function text(el) {
        return el ? (el.textContent || '').toLowerCase() : '';
    }

    function forEach(list, fn) {
        Array.prototype.forEach.call(list, fn);
    }

    onReady(function () {

        var tree = document.getElementById('doc-tree');

        /* ---- namespace tree filter ---- */

        function restoreDefaultOpen() {
            if (!tree) {
                return;
            }

            forEach(tree.querySelectorAll('.doc-ns'), function (ns) {
                ns.open = ns.getAttribute('data-default') === '1';
            });
        }

        function resetFilter() {
            if (!tree) {
                return;
            }

            forEach(tree.querySelectorAll('.doc-ns'), function (ns) {
                ns.style.display = '';
                ns.removeAttribute('data-hit');
            });

            forEach(tree.querySelectorAll('.doc-types li'), function (li) {
                li.style.display = '';
            });

            var global = tree.querySelector('.doc-global');

            if (global) {
                global.style.display = '';
            }

            restoreDefaultOpen();
        }

        function applyFilter(q) {
            if (!tree) {
                return;
            }

            if (!q) {
                resetFilter();
                return;
            }

            var nodes = Array.prototype.slice.call(tree.querySelectorAll('.doc-ns'));

            forEach(nodes, function (ns) {
                ns.removeAttribute('data-hit');
            });

            /* mark the matched nodes bottom-up: a node hits when its own label,
               one of its types, or any of its descendant nodes hits. */
            for (var i = nodes.length - 1; i >= 0; i--) {
                var ns = nodes[i];
                var label = ns.querySelector('summary a, summary .doc-node');
                var hit = text(label).indexOf(q) >= 0;

                if (!hit) {
                    var types = ns.querySelectorAll('.doc-types a');

                    for (var k = 0; k < types.length; k++) {
                        if (text(types[k]).indexOf(q) >= 0) {
                            hit = true;
                            break;
                        }
                    }
                }

                if (!hit && ns.querySelector('.doc-ns[data-hit="1"]')) {
                    hit = true;
                }

                if (hit) {
                    ns.setAttribute('data-hit', '1');
                }
            }

            forEach(nodes, function (ns) {
                var hit = ns.getAttribute('data-hit') === '1';

                ns.style.display = hit ? '' : 'none';

                if (hit) {
                    ns.open = true;
                }
            });

            /* hide the type leaves which do not match */
            forEach(tree.querySelectorAll('.doc-types li'), function (li) {
                var a = li.querySelector('a');

                li.style.display = (a && text(a).indexOf(q) >= 0) ? '' : 'none';
            });

            var global = tree.querySelector('.doc-global');

            if (global) {
                global.style.display = text(global).indexOf(q) >= 0 ? '' : 'none';
            }
        }

        var filter = document.getElementById('doc-filter');

        if (filter) {
            filter.addEventListener('input', function () {
                applyFilter(filter.value.trim().toLowerCase());
            });
        }

        /* ---- index page namespace filter ---- */

        var indexFilter = document.getElementById('index-filter');

        if (indexFilter) {
            var cards = Array.prototype.slice.call(document.querySelectorAll('#ns-grid .ns-card'));
            var counter = document.getElementById('index-count');

            var apply = function () {
                var q = indexFilter.value.trim().toLowerCase();
                var shown = 0;

                cards.forEach(function (card) {
                    var name = (card.getAttribute('data-name') || '').toLowerCase();
                    var ok = !q || name.indexOf(q) >= 0;

                    card.style.display = ok ? '' : 'none';

                    if (ok) {
                        shown++;
                    }
                });

                if (counter) {
                    counter.textContent = q
                        ? shown + ' / ' + cards.length + ' namespaces'
                        : cards.length + ' namespaces';
                }
            };

            indexFilter.addEventListener('input', apply);
            apply();
        }

        /* ---- sidebar collapse (the state is remembered in the localStorage) ---- */

        var SIDE_KEY = 'readership.sidebar';
        var toggle = document.getElementById('doc-side-toggle');

        function syncToggle(collapsed) {
            if (!toggle) {
                return;
            }

            var label = collapsed ? 'Show navigation' : 'Hide navigation';

            toggle.setAttribute('aria-expanded', collapsed ? 'false' : 'true');
            toggle.setAttribute('aria-label', label);
            toggle.title = label;
        }

        syncToggle(document.documentElement.classList.contains('side-collapsed'));

        if (toggle) {
            toggle.addEventListener('click', function () {
                var collapsed = !document.documentElement.classList.contains('side-collapsed');

                document.documentElement.classList.toggle('side-collapsed', collapsed);
                syncToggle(collapsed);

                try {
                    localStorage.setItem(SIDE_KEY, collapsed ? 'collapsed' : 'expanded');
                } catch (e) {
                    /* the localStorage could be disabled by the browser settings */
                }
            });
        }

        /* ---- smooth jump for the in page anchors ---- */

        forEach(document.querySelectorAll('a[href^="#"]'), function (a) {
            a.addEventListener('click', function (ev) {
                var id = a.getAttribute('href').slice(1);

                if (!id) {
                    return;
                }

                var target = document.getElementById(id);

                if (target) {
                    ev.preventDefault();
                    target.scrollIntoView({ behavior: 'smooth', block: 'start' });
                    history.replaceState(null, '', '#' + id);
                }
            });
        });
    });
})();
