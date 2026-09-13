/* =====================================================================
   app.js — nuget server web front end
   a small dependency free client that renders the package list, the
   database statistics and a single package detail page from the
   experimental nuget server json api.
   ===================================================================== */

(function () {
    'use strict';

    var state = {
        q: '',
        skip: 0,
        take: 15,
        total: 0
    };

    /* ----------------------------- helpers ----------------------------- */

    function $(id) {
        return document.getElementById(id);
    }

    function esc(value) {
        if (value === null || value === undefined) {
            return '';
        }
        return String(value)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;')
            .replace(/'/g, '&#39;');
    }

    function formatNumber(value) {
        var n = Number(value || 0);
        return n.toLocaleString('en-US');
    }

    function formatDate(value) {
        if (!value) {
            return '—';
        }
        var d = new Date(value);
        if (isNaN(d.getTime())) {
            return esc(value);
        }
        var pad = function (x) { return (x < 10 ? '0' : '') + x; };
        return d.getFullYear() + '-' + pad(d.getMonth() + 1) + '-' + pad(d.getDate());
    }

    function formatSize(bytes) {
        var n = Number(bytes || 0);
        if (n <= 0) {
            return '—';
        }
        if (n < 1024) {
            return n + ' B';
        }
        if (n < 1024 * 1024) {
            return (n / 1024).toFixed(1) + ' KB';
        }
        return (n / 1024 / 1024).toFixed(2) + ' MB';
    }

    function fetchJSON(url) {
        return fetch(url, { headers: { Accept: 'application/json' } }).then(function (response) {
            if (!response.ok) {
                throw new Error('HTTP ' + response.status);
            }
            return response.json();
        });
    }

    function fetchText(url) {
        return fetch(url, { headers: { Accept: 'text/plain, text/markdown' } }).then(function (response) {
            if (!response.ok) {
                throw new Error('HTTP ' + response.status);
            }
            return response.text();
        });
    }

    function hexToRgba(hex, alpha) {
        var value = String(hex || '').replace('#', '');
        if (value.length === 3) {
            value = value[0] + value[0] + value[1] + value[1] + value[2] + value[2];
        }
        var number = parseInt(value, 16);
        if (isNaN(number)) {
            return 'rgba(63,174,74,' + alpha + ')';
        }
        return 'rgba(' + ((number >> 16) & 255) + ',' + ((number >> 8) & 255) + ',' + (number & 255) + ',' + alpha + ')';
    }

    function debounce(fn, delay) {
        var timer = null;
        return function () {
            var args = arguments;
            clearTimeout(timer);
            timer = setTimeout(function () { fn.apply(null, args); }, delay);
        };
    }

    function queryParam(name) {
        var params = new URLSearchParams(window.location.search);
        return params.get(name) || '';
    }

    /* ----------------------------- statistics ----------------------------- */

    function applyStats(stats) {
        if (!stats) {
            return;
        }
        setText('stat-packages', formatNumber(stats.packages));
        setText('stat-versions', formatNumber(stats.versions));
        setText('stat-downloads', formatNumber(stats.downloads));
        setText('stat-users', formatNumber(stats.users));
        setText('stat-views', formatNumber(stats.views));
    }

    function setText(id, value) {
        var node = $(id);
        if (node) {
            node.textContent = value;
        }
    }

    function setLink(id, url, external) {
        var node = $(id);
        if (!node) {
            return;
        }
        if (!url) {
            node.textContent = '—';
            return;
        }
        node.innerHTML = '<a class="dl" href="' + esc(url) + '"' +
            (external ? ' target="_blank" rel="noopener"' : '') + '>' + esc(url) + '</a>';
    }

    function tagUrl(tag) {
        return 'tags.html?tag=' + encodeURIComponent(tag);
    }

    function renderTags(host, tags) {
        if (!host) {
            return;
        }
        var list = (tags || []);
        if (typeof list === 'string') {
            list = list.split(/[\s,;]+/);
        }
        list = list.filter(Boolean);

        if (!list.length) {
            host.innerHTML = '<span class="mono">—</span>';
            return;
        }
        host.innerHTML = list.map(function (t) {
            return '<a class="chip" href="' + tagUrl(t) + '">' + esc(t) + '</a>';
        }).join('');
    }

    function renderDependencies(host, dependencies) {
        if (!host) {
            return;
        }
        var list = dependencies || [];
        if (!list.length) {
            host.innerHTML = '<div class="empty">this package has no dependencies</div>';
            return;
        }

        var groups = {};
        list.forEach(function (d) {
            var key = d.targetFramework || '';
            (groups[key] = groups[key] || []).push(d);
        });

        var html = '';
        Object.keys(groups).forEach(function (framework) {
            html += '<div class="dep-group">';
            html += '<div class="dep-framework mono">' + esc(framework || 'any framework') + '</div>';
            html += '<ul class="dep-list">';
            groups[framework].forEach(function (d) {
                var href = d.hosted ? ('package.html?id=' + encodeURIComponent(d.id)) : d.url;
                var target = d.hosted ? '' : ' target="_blank" rel="noopener"';
                var badge = d.hosted
                    ? '<span class="dep-badge">local</span>'
                    : '<span class="dep-badge ext">nuget.org</span>';
                html += '<li><a class="dl" href="' + esc(href) + '"' + target + '>' + esc(d.id) + '</a>' +
                    (d.range ? ' <span class="mono">' + esc(d.range) + '</span>' : '') + ' ' + badge + '</li>';
            });
            html += '</ul></div>';
        });
        host.innerHTML = html;
    }

    /* ----------------------------- daily activity charts ----------------------------- */

    var trendCharts = {};

    var TREND = {
        downloads: '#3fae4a',
        views: '#6fa8dc',
        hairline: 'rgba(255,255,255,.16)',
        split: 'rgba(255,255,255,.06)',
        text: '#9a9a9a',
        strong: '#f2f2f2'
    };

    function trendSeries(points) {
        var days = [];
        var downloads = [];
        var views = [];

        (points || []).forEach(function (point) {
            days.push(point.day);
            downloads.push(Number(point.downloads || 0));
            views.push(Number(point.views || 0));
        });

        return { days: days, downloads: downloads, views: views };
    }

    function areaLine(name, data, color) {
        return {
            name: name,
            type: 'line',
            smooth: true,
            symbol: 'circle',
            symbolSize: 5,
            showSymbol: false,
            data: data,
            lineStyle: { width: 2, color: color },
            itemStyle: { color: color },
            emphasis: { focus: 'series' },
            areaStyle: {
                color: new echarts.graphic.LinearGradient(0, 0, 0, 1, [
                    { offset: 0, color: hexToRgba(color, .3) },
                    { offset: 1, color: hexToRgba(color, 0) }
                ])
            }
        };
    }

    function trendOption(series) {
        return {
            backgroundColor: 'transparent',
            grid: { left: 10, right: 16, top: 34, bottom: 4, containLabel: true },
            legend: {
                data: ['downloads', 'page views'],
                right: 6,
                top: 0,
                icon: 'roundRect',
                itemWidth: 12,
                itemHeight: 3,
                textStyle: { color: TREND.text, fontSize: 11 }
            },
            tooltip: {
                trigger: 'axis',
                backgroundColor: 'rgba(10,10,10,.94)',
                borderColor: TREND.hairline,
                borderWidth: 1,
                textStyle: { color: TREND.strong, fontSize: 12 },
                extraCssText: 'backdrop-filter: blur(6px);',
                formatter: function (params) {
                    var lines = [esc(params[0].axisValue)];
                    params.forEach(function (p) {
                        lines.push(p.marker + ' ' + esc(p.seriesName) + ' · <b>' + formatNumber(p.value) + '</b>');
                    });
                    return lines.join('<br/>');
                }
            },
            xAxis: {
                type: 'category',
                boundaryGap: false,
                data: series.days,
                axisLine: { lineStyle: { color: TREND.hairline } },
                axisTick: { show: false },
                axisLabel: { color: TREND.text, fontSize: 10.5, hideOverlap: true }
            },
            yAxis: {
                type: 'value',
                minInterval: 1,
                axisLine: { show: false },
                splitLine: { lineStyle: { color: TREND.split } },
                axisLabel: { color: TREND.text, fontSize: 10.5 }
            },
            series: [
                areaLine('downloads', series.downloads, TREND.downloads),
                areaLine('page views', series.views, TREND.views)
            ]
        };
    }

    function disposeTrend(hostId) {
        var chart = trendCharts[hostId];
        if (chart) {
            chart.dispose();
            delete trendCharts[hostId];
        }
    }

    function renderTrend(hostId, points) {
        var host = $(hostId);
        if (!host || typeof echarts === 'undefined') {
            return;
        }

        disposeTrend(hostId);

        if (!points || !points.length) {
            host.innerHTML = '<div class="empty">no activity recorded yet</div>';
            return;
        }

        host.innerHTML = '';

        var chart = echarts.init(host, null, { renderer: 'canvas' });
        chart.setOption(trendOption(trendSeries(points)));
        trendCharts[hostId] = chart;
    }

    function renderActivityTotals(prefix, result) {
        setText(prefix + '-activity-downloads', formatNumber(result.totalDownloads));
        setText(prefix + '-activity-views', formatNumber(result.totalViews));

        var host = $(prefix + '-activity-totals');
        if (host) {
            host.hidden = false;
        }
    }

    function loadActivity(hostId, prefix, url) {
        var host = $(hostId);
        if (!host) {
            return;
        }

        disposeTrend(hostId);
        host.innerHTML = '<div class="loading"><span class="spinner"></span>loading activity…</div>';

        fetchJSON(url).then(function (result) {
            renderTrend(hostId, result.points || []);
            renderActivityTotals(prefix, result);
        }).catch(function (error) {
            host.innerHTML = '<div class="empty">failed to load activity: ' + esc(error.message) + '</div>';
        });
    }

    function loadPackageActivity(id, days) {
        loadActivity('pkg-trend', 'pkg',
            '/api/activity/package/' + encodeURIComponent(id) + '?days=' + (days || 30));
    }

    function loadFeedActivity(days) {
        loadActivity('feed-trend', 'feed', '/api/activity/feed?days=' + (days || 30));
    }

    function bindTrendTools(toolsId, load) {
        var tools = $(toolsId);
        if (!tools) {
            return;
        }

        var buttons = tools.querySelectorAll('button[data-days]');

        Array.prototype.forEach.call(buttons, function (button) {
            button.addEventListener('click', function () {
                Array.prototype.forEach.call(buttons, function (other) {
                    other.classList.remove('on');
                });
                button.classList.add('on');
                load(parseInt(button.getAttribute('data-days'), 10) || 30);
            });
        });
    }

    window.addEventListener('resize', debounce(function () {
        Object.keys(trendCharts).forEach(function (key) {
            trendCharts[key].resize();
        });
    }, 150));

    /* ----------------------------- readme (marked.js) ----------------------------- */

    var markedConfigured = false;

    function configureMarked() {
        if (markedConfigured || typeof marked === 'undefined') {
            return;
        }

        markedConfigured = true;

        if (typeof marked.setOptions === 'function') {
            marked.setOptions({ gfm: true, breaks: false });
        }

        /* the readme comes from an untrusted package, so raw html is dropped */
        if (typeof marked.use === 'function') {
            marked.use({ renderer: { html: function () { return ''; } } });
        }
    }

    /* tags and attributes that must never be rendered from a readme document */
    var BLOCKED_TAGS = 'script,style,iframe,object,embed,link,meta,form,input,button,select,textarea,base';

    function sanitize(html) {
        var host = document.createElement('div');
        host.innerHTML = html;

        Array.prototype.forEach.call(host.querySelectorAll(BLOCKED_TAGS), function (node) {
            if (node.parentNode) {
                node.parentNode.removeChild(node);
            }
        });

        Array.prototype.forEach.call(host.querySelectorAll('*'), function (node) {
            Array.prototype.slice.call(node.attributes || []).forEach(function (attribute) {
                var name = attribute.name.toLowerCase();
                var value = String(attribute.value || '').replace(/\s+/g, '').toLowerCase();

                if (name.indexOf('on') === 0) {
                    node.removeAttribute(attribute.name);
                } else if ((name === 'href' || name === 'src' || name === 'xlink:href') &&
                    (value.indexOf('javascript:') === 0 || value.indexOf('vbscript:') === 0)) {
                    node.removeAttribute(attribute.name);
                }
            });
        });

        return host;
    }

    function loadPackageReadme(pkg) {
        var section = $('readme-section');
        var host = $('pkg-readme');

        if (!section || !host) {
            return;
        }

        var info = pkg.readme || {};
        if (!info.available || !info.url) {
            section.hidden = true;
            return;
        }

        section.hidden = false;
        setText('pkg-readme-file', info.file || 'README');
        host.innerHTML = '<div class="loading"><span class="spinner"></span>loading readme…</div>';

        fetchText(info.url).then(function (text) {
            if (info.markdown && typeof marked !== 'undefined') {
                configureMarked();

                var html = typeof marked.parse === 'function' ? marked.parse(text) : marked(text);
                var clean = sanitize(html);

                host.innerHTML = '';
                while (clean.firstChild) {
                    host.appendChild(clean.firstChild);
                }
            } else {
                host.innerHTML = '';
                var pre = document.createElement('pre');
                pre.className = 'readme-raw';
                pre.textContent = text;
                host.appendChild(pre);
            }
        }).catch(function () {
            section.hidden = true;
        });
    }

    /* ----------------------------- kmeans cluster label ----------------------------- */

    /* the cluster colours must match the scatter palette of the graphs page so
       that the label chip and the 3d point agree. */
    var CLUSTER_COLORS = [
        '#3fae4a', '#6fa8dc', '#d9a94a', '#c07ad6', '#5cc46a', '#ff7a6e',
        '#4fb0c6', '#b8d94a', '#e08f4a', '#8f7ae0', '#4ad6a5', '#d9d24a'
    ];

    function renderClusterLabel(cluster) {
        var row = $('pkg-cluster-row');
        if (!row) {
            return;
        }

        if (!cluster || !cluster.available) {
            row.hidden = true;
            return;
        }

        var index = (Number(cluster.label) - 1) % CLUSTER_COLORS.length;
        if (index < 0) {
            index += CLUSTER_COLORS.length;
        }

        var node = $('pkg-cluster');
        if (node) {
            /* the label of the meta row already reads "Cluster", so only the
               kmeans label number is rendered here. */
            node.textContent = String(cluster.label);
            node.style.color = CLUSTER_COLORS[index];
        }

        row.hidden = false;
        row.title = 'umap embedding: ' + Number(cluster.x || 0).toFixed(3) + ', ' +
            Number(cluster.y || 0).toFixed(3) + ', ' + Number(cluster.z || 0).toFixed(3);
    }

    /* ----------------------------- api documentation link ----------------------------- */

    /* the package detail page links to the server side rendered api document index
       page of the package; the link is hidden when the package ships no document. */
    function renderDocsLink(pkg) {
        var row = $('pkg-docs-row');
        if (!row) {
            return;
        }

        var docs = pkg.docs || {};

        if (!docs.available || !docs.url) {
            row.hidden = true;
            return;
        }

        var link = $('pkg-docs-link');
        if (link) {
            link.href = docs.url;
            link.textContent = 'browse the api documentation';
        }

        var count = Number(docs.typeCount || 0);
        setText('pkg-docs-meta', count + ' type' + (count === 1 ? '' : 's') +
            (docs.version ? ' · version ' + docs.version : ''));

        row.hidden = false;
    }

    /* ----------------------------- package list ----------------------------- */

    function loadPackages() {
        var host = $('package-list');
        if (!host) {
            return;
        }

        host.innerHTML = '<div class="loading"><span class="spinner"></span>loading packages…</div>';

        var url = '/api/packages?skip=' + state.skip + '&take=' + state.take +
            '&q=' + encodeURIComponent(state.q);

        fetchJSON(url).then(function (result) {
            state.total = result.total || 0;
            renderPackages(result.packages || []);
            renderPager();
        }).catch(function (error) {
            host.innerHTML = '<div class="empty">failed to load packages: ' + esc(error.message) + '</div>';
        });
    }

    function renderPackages(packages) {
        var host = $('package-list');
        if (!host) {
            return;
        }

        if (!packages.length) {
            host.innerHTML = '<div class="empty">no packages found</div>';
            return;
        }

        var rows = packages.map(function (pkg) {
            var id = pkg.id || '';
            var tags = (pkg.tags || '').split(/[\s,;]+/).filter(Boolean).slice(0, 4);
            var tagHtml = tags.map(function (t) {
                return '<span class="ver">' + esc(t) + '</span>';
            }).join(' ');

            return '<tr class="row-link" data-id="' + esc(encodeURIComponent(id)) + '">' +
                '<td><span class="pkg-name">' + esc(id) + '</span>' +
                '<span class="pkg-desc">' + esc(pkg.description || '—') + '</span>' +
                (pkg.latestVersion
                    ? '<span class="ver pkg-ver">' + esc(pkg.latestVersion) + '</span>'
                    : '') +
                '</td>' +
                '<td class="num">' + formatNumber(pkg.totalDownloads) + '</td>' +
                '<td class="num">' + formatNumber(pkg.versions) + '</td>' +
                '<td class="mono">' + formatDate(pkg.published) + '</td>' +
                '<td>' + (tagHtml || '<span class="mono">—</span>') + '</td>' +
                '</tr>';
        }).join('');

        /* the latest version is rendered inside the package cell (as its third
           line) so that the table stays narrow. */
        host.innerHTML = '<div class="tablewrap fade-in"><table>' +
            '<thead><tr>' +
            '<th>Package</th><th class="num">Downloads</th>' +
            '<th class="num">Versions</th><th>Published</th><th>Tags</th>' +
            '</tr></thead><tbody>' + rows + '</tbody></table></div>';

        Array.prototype.forEach.call(host.querySelectorAll('tr.row-link'), function (row) {
            row.addEventListener('click', function () {
                window.location.href = 'package.html?id=' + row.getAttribute('data-id');
            });
        });
    }

    function renderPager() {
        var info = $('page-info');
        var prev = $('page-prev');
        var next = $('page-next');
        if (!info) {
            return;
        }

        var from = state.total === 0 ? 0 : state.skip + 1;
        var to = Math.min(state.skip + state.take, state.total);
        info.textContent = from + '–' + to + ' of ' + state.total;

        if (prev) {
            prev.disabled = state.skip <= 0;
        }
        if (next) {
            next.disabled = state.skip + state.take >= state.total;
        }
    }

    /* ----------------------------- package detail ----------------------------- */

    function loadPackageDetail() {
        var host = $('package-detail');
        if (!host) {
            return;
        }

        var id = queryParam('id');
        if (!id) {
            host.innerHTML = '<div class="empty">missing package id in the url query string</div>';
            return;
        }

        host.innerHTML = '<div class="loading"><span class="spinner"></span>loading package…</div>';

        fetchJSON('/api/package/' + encodeURIComponent(id)).then(function (pkg) {
            renderPackageDetail(pkg);
        }).catch(function (error) {
            host.innerHTML = '<div class="empty">failed to load package "' + esc(id) + '": ' + esc(error.message) + '</div>';
        });
    }

    function renderPackageDetail(pkg) {
        document.title = (pkg.id || 'package') + ' · nuget';

        var placeholder = $('package-detail');
        if (placeholder) {
            placeholder.innerHTML = '';
            placeholder.style.display = 'none';
        }

        var headline = $('pkg-headline');
        if (headline) {
            headline.innerHTML = '<span class="u">' + esc(pkg.id) + '</span>';
        }

        var crumb = $('pkg-crumb');
        if (crumb) {
            crumb.textContent = pkg.id || '';
        }

        setText('pkg-latest', pkg.latestVersion || '');
        setText('pkg-downloads', formatNumber(pkg.totalDownloads));
        setText('pkg-versions', formatNumber((pkg.versions || []).length));
        setText('pkg-published', formatDate(pkg.published));
        renderClusterLabel(pkg.cluster);
        renderDocsLink(pkg);

        var titleNode = $('pkg-headline');
        if (titleNode) {
            titleNode.innerHTML = '<span class="u">' + esc(pkg.title || pkg.id) + '</span>';
        }

        var iconNode = $('pkg-icon');
        if (iconNode) {
            if (pkg.iconUrl) {
                iconNode.src = pkg.iconUrl;
                iconNode.alt = (pkg.id || '') + ' icon';
                iconNode.style.display = '';
            } else {
                iconNode.style.display = 'none';
            }
        }

        var summaryNode = $('pkg-summary');
        if (summaryNode) {
            summaryNode.textContent = pkg.summary || '';
        }

        setLink('pkg-project', pkg.projectUrl, true);
        setLink('pkg-repository', pkg.repository, true);
        setLink('pkg-license-url', pkg.licenseUrl, true);

        setText('pkg-authors', pkg.authors || '—');
        setText('pkg-owners', pkg.owners || '—');
        setText('pkg-license', pkg.license || '—');
        setText('pkg-language', pkg.language || '—');
        setText('pkg-copyright', pkg.copyright || '—');
        setText('pkg-require-license', String(pkg.requireLicenseAcceptance || 'false'));

        renderTags($('pkg-tags'), pkg.tags);
        renderDependencies($('pkg-dependencies'), pkg.dependencies);

        var desc = $('pkg-description');
        if (desc) {
            desc.textContent = pkg.description || 'no description provided.';
        }

        var notes = $('pkg-release-notes');
        if (notes) {
            notes.textContent = pkg.releaseNotes || '—';
        }

        var nuspec = $('pkg-nuspec');
        if (nuspec) {
            nuspec.textContent = (pkg.metadata && pkg.metadata.nuspec) || 'not available';
        }

        var versionHost = $('version-list');
        if (versionHost) {
            var versions = pkg.versions || [];
            if (!versions.length) {
                versionHost.innerHTML = '<div class="empty">no published versions</div>';
            } else {
                var rows = versions.slice().reverse().map(function (v) {
                    return '<tr>' +
                        '<td><span class="ver">' + esc(v.version) + '</span></td>' +
                        '<td class="num">' + formatNumber(v.downloads) + '</td>' +
                        '<td class="num">' + formatSize(v.size) + '</td>' +
                        '<td class="mono">' + formatDate(v.published) + '</td>' +
                        '<td><a class="dl" href="' + esc(v.downloadUrl) + '">download</a></td>' +
                        '</tr>';
                }).join('');

                versionHost.innerHTML = '<div class="tablewrap fade-in"><table>' +
                    '<thead><tr><th>Version</th><th class="num">Downloads</th>' +
                    '<th class="num">Size</th><th>Published</th><th>Package</th></tr></thead>' +
                    '<tbody>' + rows + '</tbody></table></div>';
            }
        }

        loadPackageReadme(pkg);

        bindTrendTools('pkg-trend-tools', function (days) {
            loadPackageActivity(pkg.id, days);
        });
        loadPackageActivity(pkg.id, 30);
    }

    /* ----------------------------- about page ----------------------------- */

    function loadAbout() {
        var host = $('about-stats');
        if (!host) {
            return;
        }

        fetchJSON('/api/stats').then(function (result) {
            applyStats(result.stats);
            renderTop(result.topDownloads || []);
            renderRecent(result.recent || []);
            setText('about-generated', formatDate(result.generated));
        }).catch(function (error) {
            host.innerHTML = '<div class="empty">failed to load statistics: ' + esc(error.message) + '</div>';
        });

        bindTrendTools('feed-trend-tools', loadFeedActivity);
        loadFeedActivity(30);
    }

    function renderTop(items) {
        var host = $('top-downloads');
        if (!host) {
            return;
        }
        if (!items.length) {
            host.innerHTML = '<div class="empty">no data yet</div>';
            return;
        }

        var rows = items.map(function (item) {
            return '<tr class="row-link" data-id="' + esc(encodeURIComponent(item.id)) + '">' +
                '<td><span class="pkg-name">' + esc(item.id) + '</span></td>' +
                '<td><span class="ver">' + esc(item.latestVersion) + '</span></td>' +
                '<td class="num">' + formatNumber(item.versions) + '</td>' +
                '<td class="num">' + formatNumber(item.downloads) + '</td>' +
                '</tr>';
        }).join('');

        host.innerHTML = '<div class="tablewrap"><table>' +
            '<thead><tr><th>Package</th><th>Latest</th><th class="num">Versions</th>' +
            '<th class="num">Downloads</th></tr></thead><tbody>' + rows + '</tbody></table></div>';

        Array.prototype.forEach.call(host.querySelectorAll('tr.row-link'), function (row) {
            row.addEventListener('click', function () {
                window.location.href = 'package.html?id=' + row.getAttribute('data-id');
            });
        });
    }

    function renderRecent(items) {
        var host = $('recent-packages');
        if (!host) {
            return;
        }
        if (!items.length) {
            host.innerHTML = '<div class="empty">no data yet</div>';
            return;
        }

        var rows = items.map(function (item) {
            return '<tr class="row-link" data-id="' + esc(encodeURIComponent(item.id)) + '">' +
                '<td><span class="pkg-name">' + esc(item.id) + '</span></td>' +
                '<td><span class="ver">' + esc(item.version) + '</span></td>' +
                '<td class="num">' + formatNumber(item.downloads) + '</td>' +
                '<td class="mono">' + formatDate(item.published) + '</td>' +
                '</tr>';
        }).join('');

        host.innerHTML = '<div class="tablewrap"><table>' +
            '<thead><tr><th>Package</th><th>Version</th><th class="num">Downloads</th>' +
            '<th>Published</th></tr></thead><tbody>' + rows + '</tbody></table></div>';

        Array.prototype.forEach.call(host.querySelectorAll('tr.row-link'), function (row) {
            row.addEventListener('click', function () {
                window.location.href = 'package.html?id=' + row.getAttribute('data-id');
            });
        });
    }

    /* ----------------------------- bootstrap ----------------------------- */

    function initIndex() {
        applyStats(null);

        fetchJSON('/api/stats').then(function (result) {
            applyStats(result.stats);
        }).catch(function () { });

        loadPackages();

        var search = $('search-input');
        if (search) {
            search.addEventListener('input', debounce(function () {
                state.q = search.value.trim();
                state.skip = 0;
                loadPackages();
            }, 260));
        }

        var prev = $('page-prev');
        if (prev) {
            prev.addEventListener('click', function () {
                state.skip = Math.max(0, state.skip - state.take);
                loadPackages();
            });
        }

        var next = $('page-next');
        if (next) {
            next.addEventListener('click', function () {
                if (state.skip + state.take < state.total) {
                    state.skip += state.take;
                    loadPackages();
                }
            });
        }
    }

    /* ----------------------------- tag query page ----------------------------- */

    var tagState = { tag: '', skip: 0, take: 20, total: 0 };

    function initTags() {
        tagState.tag = queryParam('tag');

        var label = $('tag-name');
        if (label) {
            label.textContent = tagState.tag || '(none)';
        }
        document.title = (tagState.tag || 'tag') + ' · nuget';

        loadTagPackages();

        var prev = $('page-prev');
        if (prev) {
            prev.addEventListener('click', function () {
                tagState.skip = Math.max(0, tagState.skip - tagState.take);
                loadTagPackages();
            });
        }

        var next = $('page-next');
        if (next) {
            next.addEventListener('click', function () {
                if (tagState.skip + tagState.take < tagState.total) {
                    tagState.skip += tagState.take;
                    loadTagPackages();
                }
            });
        }
    }

    function loadTagPackages() {
        var host = $('package-list');
        if (!host) {
            return;
        }
        if (!tagState.tag) {
            host.innerHTML = '<div class="empty">no tag was specified in the url query string</div>';
            return;
        }

        host.innerHTML = '<div class="loading"><span class="spinner"></span>loading packages…</div>';

        fetchJSON('/api/tag/' + encodeURIComponent(tagState.tag) + '?skip=' + tagState.skip + '&take=' + tagState.take)
            .then(function (result) {
                tagState.total = result.total || 0;
                renderPackages(result.packages || []);
                renderTagPager();
            })
            .catch(function (error) {
                host.innerHTML = '<div class="empty">failed to load the tag: ' + esc(error.message) + '</div>';
            });
    }

    function renderTagPager() {
        var info = $('page-info');
        var prev = $('page-prev');
        var next = $('page-next');
        if (!info) {
            return;
        }

        var from = tagState.total === 0 ? 0 : tagState.skip + 1;
        var to = Math.min(tagState.skip + tagState.take, tagState.total);
        info.textContent = from + '–' + to + ' of ' + tagState.total;

        if (prev) {
            prev.disabled = tagState.skip <= 0;
        }
        if (next) {
            next.disabled = tagState.skip + tagState.take >= tagState.total;
        }
    }

    document.addEventListener('DOMContentLoaded', function () {
        var page = document.body.getAttribute('data-page');

        if (page === 'index') {
            initIndex();
        } else if (page === 'package') {
            loadPackageDetail();
        } else if (page === 'about') {
            loadAbout();
        } else if (page === 'tags') {
            initTags();
        }
    });
})();
