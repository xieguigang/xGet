/* =====================================================================
   docs.js — api reference document interactions
   + sidebar filter
   + index page namespace filter
   + keep the active type visible in the sidebar
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

    onReady(function () {

        /* ---- sidebar filter ---- */
        var filter = document.getElementById('doc-filter');

        if (filter) {
            filter.addEventListener('input', function () {
                var q = filter.value.trim().toLowerCase();
                var groups = document.querySelectorAll('.doc-tree .doc-ns');

                Array.prototype.forEach.call(groups, function (ns) {
                    var name = text(ns.querySelector('summary a'));
                    var matched = !q || name.indexOf(q) >= 0;

                    if (!matched && q) {
                        var types = ns.querySelectorAll('.doc-types a');

                        for (var i = 0; i < types.length; i++) {
                            if (text(types[i]).indexOf(q) >= 0) {
                                matched = true;
                                break;
                            }
                        }
                    }

                    ns.style.display = matched ? '' : 'none';

                    if (q && matched) {
                        ns.open = true;
                    }
                });
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

        /* ---- keep the active type visible ---- */
        var active = document.querySelector('.doc-types a.on');

        if (active) {
            var owner = active.closest('.doc-ns');

            if (owner) {
                owner.open = true;
            }

            var side = document.querySelector('.doc-side');

            if (side && active.offsetTop > side.clientHeight) {
                side.scrollTop = active.offsetTop - side.clientHeight / 2;
            }
        }

        /* ---- smooth jump for the in page anchors ---- */
        document.querySelectorAll('a[href^="#"]').forEach(function (a) {
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
