/* =====================================================================
   charts.js — dark themed statistics visualisation of the nuget feed
   the page reads the precomputed statistics from the server json api and
   renders them with echarts (word cloud + bar + force graph) and
   3d-force-graph (three.js based) for the tag relation network.
   ===================================================================== */

(function () {
    'use strict';

    var TEXT = '#9a9a9a';
    var TEXT_STRONG = '#f2f2f2';
    var HAIRLINE = 'rgba(255,255,255,.16)';
    var ACCENT = '#3fae4a';
    var BG = '#030303';
    var PALETTE = ['#3fae4a', '#5cc46a', '#2e8b3a', '#6fa8dc', '#e0c24a', '#9a9a9a', '#1f6b2a', '#ff7a6e'];

    /* the cluster palette of the umap + kmeans scatter: the scibasic green and
       blue family extended with the hues needed by a custom k. */
    var CLUSTER_PALETTE = [
        '#3fae4a', '#6fa8dc', '#d9a94a', '#c07ad6', '#5cc46a', '#ff7a6e',
        '#4fb0c6', '#b8d94a', '#e08f4a', '#8f7ae0', '#4ad6a5', '#d9d24a'
    ];

    var echartsInstances = [];

    var tooltip = {
        backgroundColor: 'rgba(10,10,10,.94)',
        borderColor: HAIRLINE,
        borderWidth: 1,
        textStyle: { color: TEXT_STRONG, fontSize: 12 },
        extraCssText: 'backdrop-filter: blur(6px);'
    };

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
            .replace(/>/g, '&gt;');
    }

    function fetchJSON(url) {
        return fetch(url, { headers: { Accept: 'application/json' } }).then(function (response) {
            if (!response.ok) {
                throw new Error('HTTP ' + response.status);
            }
            return response.json();
        });
    }

    function showEmpty(id, message) {
        var host = $(id);
        if (host) {
            host.innerHTML = '<div class="empty">' + esc(message) + '</div>';
        }
    }

    function showLoading(id, message) {
        var host = $(id);
        if (host) {
            host.innerHTML = '<div class="loading"><span class="spinner"></span>' + esc(message) + '</div>';
        }
    }

    function setText(id, value) {
        var node = $(id);
        if (node) {
            node.textContent = value;
        }
    }

    function packageLink(id, hosted) {
        return hosted
            ? ('package.html?id=' + encodeURIComponent(id))
            : ('https://www.nuget.org/packages/' + encodeURIComponent(id));
    }

    function bindResize() {
        window.addEventListener('resize', function () {
            echartsInstances.forEach(function (chart) {
                if (chart && chart.resize) {
                    chart.resize();
                }
            });
        });
    }

    /* ------------------------------ word cloud ------------------------------ */

    function renderTagCloud(containerId, tags) {
        var host = $(containerId);
        if (!host) {
            return null;
        }
        host.innerHTML = '';

        var chart = echarts.init(host, null, { renderer: 'canvas' });
        echartsInstances.push(chart);

        chart.setOption({
            backgroundColor: 'transparent',
            tooltip: Object.assign({
                formatter: function (p) {
                    return esc(p.name) + ' · <b>' + p.value + '</b> package(s)';
                }
            }, tooltip),
            series: [{
                type: 'wordCloud',
                shape: 'circle',
                left: 'center',
                top: 'center',
                width: '92%',
                height: '90%',
                sizeRange: [14, 62],
                rotationRange: [-45, 45],
                rotationStep: 15,
                gridSize: 10,
                drawOutOfBound: false,
                textStyle: {
                    fontFamily: 'Inter, system-ui, sans-serif',
                    fontWeight: 500,
                    color: function () {
                        return PALETTE[Math.floor(Math.random() * PALETTE.length)];
                    }
                },
                emphasis: {
                    textStyle: {
                        textShadowBlur: 14,
                        textShadowColor: 'rgba(63,174,74,.65)'
                    }
                },
                data: tags.map(function (t) {
                    return { name: t.name, value: t.count };
                })
            }]
        });

        chart.on('click', function (params) {
            if (params && params.name) {
                window.location.href = 'tags.html?tag=' + encodeURIComponent(params.name);
            }
        });

        return chart;
    }

    /* ------------------------------ bar chart ------------------------------ */

    function renderTagBar(containerId, tags) {
        var host = $(containerId);
        if (!host) {
            return null;
        }
        host.innerHTML = '';

        var top = tags.slice(0, 25).slice().reverse();
        var chart = echarts.init(host, null, { renderer: 'canvas' });
        echartsInstances.push(chart);

        chart.setOption({
            backgroundColor: 'transparent',
            grid: { left: 8, right: 40, top: 12, bottom: 8, containLabel: true },
            tooltip: Object.assign({
                trigger: 'axis',
                axisPointer: { type: 'shadow', shadowStyle: { color: 'rgba(255,255,255,.04)' } },
                formatter: function (params) {
                    var p = params[0];
                    return esc(p.name) + ' · <b>' + p.value + '</b> package(s)';
                }
            }, tooltip),
            xAxis: {
                type: 'value',
                axisLine: { lineStyle: { color: HAIRLINE } },
                splitLine: { lineStyle: { color: 'rgba(255,255,255,.06)' } },
                axisLabel: { color: TEXT, fontSize: 11 }
            },
            yAxis: {
                type: 'category',
                data: top.map(function (t) { return t.name; }),
                axisLine: { lineStyle: { color: HAIRLINE } },
                axisTick: { show: false },
                axisLabel: { color: TEXT, fontSize: 11 }
            },
            series: [{
                type: 'bar',
                data: top.map(function (t) { return t.count; }),
                barWidth: '62%',
                itemStyle: {
                    borderRadius: [0, 2, 2, 0],
                    color: new echarts.graphic.LinearGradient(0, 0, 1, 0, [
                        { offset: 0, color: '#1f6b2a' },
                        { offset: 1, color: '#3fae4a' }
                    ])
                },
                emphasis: {
                    itemStyle: { color: '#5cc46a' }
                },
                label: {
                    show: true,
                    position: 'right',
                    color: TEXT,
                    fontSize: 10.5,
                    fontFamily: 'Inter, system-ui, sans-serif'
                }
            }]
        });

        chart.on('click', function (params) {
            if (params && params.name) {
                window.location.href = 'tags.html?tag=' + encodeURIComponent(params.name);
            }
        });

        return chart;
    }

    /* --------------------- tag relation network (3d) --------------------- */

    function renderTagNetwork(containerId, graph) {
        var host = $(containerId);
        if (!host) {
            return null;
        }
        host.innerHTML = '';

        if (!graph || !graph.nodes || graph.nodes.length === 0) {
            showEmpty(containerId, 'no tag relation data yet');
            return null;
        }

        var nodes = graph.nodes.map(function (n) {
            return {
                id: n.id,
                name: n.name,
                tags: n.tags || [],
                value: n.value || (n.tags ? n.tags.length : 1)
            };
        });
        var maxValue = nodes.reduce(function (a, n) { return Math.max(a, n.value); }, 1);

        var graph3d = ForceGraph3D({
            controlType: 'orbit',
            rendererConfig: { antialias: true, alpha: false, preserveDrawingBuffer: true }
        })(host)
            .backgroundColor(BG)
            .showNavInfo(false)
            .nodeId('id')
            .nodeLabel(function (n) {
                return '<div style="color:#f2f2f2;font:12px Inter,sans-serif">'
                    + '<b>' + esc(n.name) + '</b><br/>'
                    + '<span style="color:#9a9a9a">' + esc((n.tags || []).join(', ')) + '</span></div>';
            })
            .nodeVal(function (n) { return 1 + 6 * (n.value / maxValue); })
            .nodeColor(function (n) {
                var ratio = n.value / maxValue;
                return ratio > 0.66 ? '#5cc46a' : (ratio > 0.33 ? '#3fae4a' : '#2e8b3a');
            })
            .nodeOpacity(0.92)
            .nodeResolution(12)
            .linkWidth(function (l) { return Math.min(4, 0.4 + (l.weight || 1) * 0.5); })
            .linkColor(function (l) {
                return 'rgba(63,174,74,' + Math.min(0.75, 0.15 + (l.weight || 1) * 0.12) + ')';
            })
            .linkDirectionalParticles(0)
            .warmupTicks(40)
            .cooldownTicks(120)
            .graphData({
                nodes: nodes,
                links: graph.links.map(function (l) {
                    return { source: l.source, target: l.target, weight: l.weight || 1 };
                })
            })
            .onNodeClick(function (node) {
                // every node of the tag relation network is a package hosted in
                // this feed, so it always links to the local detail page.
                window.location.href = packageLink(node.id, true);
            });

        // gently auto rotate for a lively 3d feel
        var angle = 0;
        var controls = graph3d.controls();
        if (controls && controls.autoRotate !== undefined) {
            controls.autoRotate = true;
            controls.autoRotateSpeed = 0.6;
        }

        return graph3d;
    }

    /* --------------------- package clusters (umap + kmeans) --------------------- */

    function clusterColor(label) {
        var index = (Number(label) - 1) % CLUSTER_PALETTE.length;
        if (index < 0) {
            index += CLUSTER_PALETTE.length;
        }
        return CLUSTER_PALETTE[index];
    }

    /* the shared state of the graphs page: the cluster document is cached so
       that a cluster filter can aggregate the tags of the selected packages
       without another request to the server. */
    var graphState = {
        clusters: null,
        filter: 0,
        allTags: [],
        totalTags: 0,
        totalPackages: 0,
        tagCloud: null,
        tagBar: null
    };

    function renderClusterLegend(clusters, selected) {
        var host = $('cluster-legend');
        if (!host) {
            return;
        }

        var list = clusters || [];
        if (!list.length) {
            host.innerHTML = '';
            return;
        }

        var active = Number(selected) || 0;
        var chips = list.map(function (c) {
            var on = Number(c.label) === active ? ' on' : '';

            return '<button type="button" class="legend-item' + on + '" data-cluster="' +
                esc(c.label) + '" title="filter the tag charts by cluster ' + esc(c.label) + '">' +
                '<span class="dot" style="background:' + clusterColor(c.label) + '"></span>' +
                'cluster ' + esc(c.label) + ' · ' + esc(c.size) + '</button>';
        });

        chips.push('<button type="button" class="legend-item all' + (active ? '' : ' on') +
            '" data-cluster="0" title="show the tags of every package">All</button>');

        host.innerHTML = chips.join('');
    }

    /* --------------------- cluster filter (tag charts) --------------------- */

    /* aggregate the tag frequency of the given packages */
    function aggregateTags(points) {
        var counter = {};

        (points || []).forEach(function (p) {
            (p.tags || []).forEach(function (tag) {
                var key = String(tag).trim();

                if (key) {
                    counter[key] = (counter[key] || 0) + 1;
                }
            });
        });

        return Object.keys(counter).map(function (name) {
            return { name: name, count: counter[name] };
        }).sort(function (a, b) {
            if (b.count !== a.count) {
                return b.count - a.count;
            }
            return a.name < b.name ? -1 : 1;
        });
    }

    function disposeChart(containerId) {
        var host = $(containerId);

        if (!host) {
            return;
        }

        var chart = echarts.getInstanceByDom(host);
        var index = echartsInstances.indexOf(chart);

        if (chart) {
            chart.dispose();
        }
        if (index >= 0) {
            echartsInstances.splice(index, 1);
        }
    }

    function renderTagCharts(list, source) {
        /* the tag charts are re-rendered on every filter change, so the
           previous instances must be released first, otherwise the canvases
           would stack up and the instance registry would keep growing. */
        disposeChart('chart-tag-cloud');
        disposeChart('chart-tag-bar');

        if (!list.length) {
            graphState.tagCloud = null;
            graphState.tagBar = null;
            showEmpty('chart-tag-cloud', 'no tags for ' + source);
            showEmpty('chart-tag-bar', 'no tags for ' + source);
            return;
        }

        graphState.tagCloud = renderTagCloud('chart-tag-cloud', list);
        graphState.tagBar = renderTagBar('chart-tag-bar', list);
    }

    /* apply the cluster filter to the tag charts: cluster 0 means "all" */
    function applyClusterFilter(cluster) {
        var selected = Number(cluster) || 0;
        var doc = graphState.clusters;
        var points = (doc && doc.points) || [];
        var tags;
        var source;

        graphState.filter = selected;
        renderClusterLegend(doc && doc.clusters, selected);

        if (selected) {
            var filtered = points.filter(function (p) {
                return Number(p.cluster) === selected;
            });

            tags = aggregateTags(filtered);
            source = 'cluster ' + selected + ' · ' + filtered.length +
                ' package' + (filtered.length === 1 ? '' : 's');

            /* the distinct tags card follows the current filter, while the All
               chip restores the global value of the feed. */
            setText('stat-graph-tags', tags.length);

            var info = $('cluster-filter-info');
            if (info) {
                info.textContent = 'cluster ' + selected + ' · ' + filtered.length + ' packages';
            }
        } else {
            /* "all" uses the server side tag distribution of the whole feed
               (which also counts the packages that have no tag at all). */
            tags = graphState.allTags.length ? graphState.allTags : aggregateTags(points);
            source = 'all packages';

            setText('stat-graph-tags', graphState.totalTags || tags.length);

            var all = $('cluster-filter-info');
            if (all) {
                all.textContent = 'all packages';
            }
        }

        renderTagCharts(tags, source);
        setText('tag-cloud-source', source);
        setText('tag-bar-source', source);
    }

    function renderPackageClusters(containerId, doc) {
        var host = $(containerId);
        if (!host) {
            return null;
        }
        host.innerHTML = '';
        graphState.clusters = doc || null;

        renderClusterLegend(doc && doc.clusters, graphState.filter);

        var points = (doc && doc.points) || [];
        if (!points.length) {
            showEmpty(containerId, (doc && doc.message) || 'the cluster analysis has not been built yet');
            return null;
        }

        var sizes = {};
        (doc.clusters || []).forEach(function (c) { sizes[c.label] = c.size; });

        var maxSize = 1;
        Object.keys(sizes).forEach(function (label) {
            maxSize = Math.max(maxSize, Number(sizes[label]) || 1);
        });

        /* the echarts-gl scatter3D pipeline copies the item level symbolSize /
           symbol / style into the item visuals, so the point size can encode
           the cluster size while the colour encodes the cluster label. */
        var data = points.map(function (p) {
            var cluster = Number(p.cluster) || 0;
            var size = Number(sizes[cluster]) || 1;

            return {
                value: [Number(p.x) || 0, Number(p.y) || 0, Number(p.z) || 0],
                name: p.name || p.id,
                id: p.id,
                cluster: cluster,
                tags: p.tags || [],
                symbolSize: 6 + 7 * Math.sqrt(size / maxSize),
                itemStyle: { color: clusterColor(cluster), opacity: 0.92 }
            };
        });

        /* one value axis per umap dimension, padded a little so that no point
           sits exactly on the grid border */
        function axis3D(name, index) {
            var values = data.map(function (item) { return item.value[index]; });
            var min = Math.min.apply(null, values);
            var max = Math.max.apply(null, values);
            var span = (max - min) || 1;
            var padding = span * 0.08;

            return {
                type: 'value',
                name: name,
                min: min - padding,
                max: max + padding,
                nameTextStyle: { color: TEXT_STRONG, fontSize: 12, fontWeight: 500 },
                nameGap: 18,
                splitNumber: 3,
                axisLine: { lineStyle: { color: HAIRLINE } },
                axisTick: { lineStyle: { color: HAIRLINE } },
                axisLabel: {
                    color: TEXT,
                    fontSize: 10,
                    margin: 6,
                    /* the raw umap values are long floats, so the ticks are
                       rounded for readability */
                    formatter: function (value) {
                        return Number(value).toFixed(1);
                    }
                },
                splitLine: { lineStyle: { color: 'rgba(255,255,255,.06)' } },
                splitArea: { show: false }
            };
        }

        var chart = echarts.init(host, null, { renderer: 'canvas' });
        echartsInstances.push(chart);

        chart.setOption({
            backgroundColor: 'transparent',
            tooltip: Object.assign({
                formatter: function (p) {
                    var item = p.data || {};
                    var name = item.name || p.name || '';
                    var tags = item.tags || [];

                    return '<b>' + esc(name) + '</b><br/>'
                        + '<span style="color:#9a9a9a">cluster ' + esc(item.cluster) + '</span><br/>'
                        + '<span style="color:#9a9a9a">' + esc(tags.join(', ')) + '</span>';
                }
            }, tooltip),
            grid3D: {
                boxWidth: 100,
                boxHeight: 66,
                boxDepth: 100,
                left: 'center',
                top: 10,
                bottom: 10,
                axisPointer: { show: false },
                axisLine: { lineStyle: { color: HAIRLINE } },
                splitLine: { lineStyle: { color: 'rgba(255,255,255,.06)' } },
                viewControl: {
                    autoRotate: true,
                    autoRotateSpeed: 6,
                    distance: 205,
                    alpha: 20,
                    beta: 30,
                    rotateMouseButton: 'left',
                    panMouseButton: 'right'
                }
            },
            xAxis3D: axis3D('UMAP1', 0),
            yAxis3D: axis3D('UMAP2', 1),
            zAxis3D: axis3D('UMAP3', 2),
            series: [{
                type: 'scatter3D',
                name: 'packages',
                symbol: 'circle',
                data: data,
                emphasis: { itemStyle: { opacity: 1 } }
            }]
        });

        chart.on('click', function (params) {
            if (params && params.data && params.data.id) {
                window.location.href = packageLink(params.data.id, true);
            }
        });

        /* published for debugging and for the end to end assertions */
        window.__packageClusterChart = chart;

        return chart;
    }

    /* ----------------------- dependency network ----------------------- */

    function renderDependencyNetwork(containerId, graph) {
        var host = $(containerId);
        if (!host) {
            return null;
        }
        host.innerHTML = '';

        if (!graph || !graph.nodes || graph.nodes.length === 0) {
            showEmpty(containerId, 'no dependency data yet');
            return null;
        }

        var degree = {};
        (graph.links || []).forEach(function (l) {
            degree[l.source] = (degree[l.source] || 0) + 1;
            degree[l.target] = (degree[l.target] || 0) + 1;
        });

        /* the project colour grouping: every distinct host of the nuspec
           projectUrl of a hosted package forms one colour group, packages
           without a project url are grey and the external dependencies stay
           blue. the group list is ordered by the node count so that the
           legend puts the biggest projects first. */
        var NO_PROJECT = '(no project)';
        var EXTERNAL = 'external';
        var projectHosts = {};

        graph.nodes.forEach(function (n) {
            if (n.external === true) {
                return;
            }
            var g = n.project ? String(n.project) : NO_PROJECT;
            projectHosts[g] = (projectHosts[g] || 0) + 1;
        });

        var groups = Object.keys(projectHosts).sort(function (a, b) {
            return projectHosts[b] - projectHosts[a] || a.localeCompare(b);
        });

        var groupColor = {};
        groups.forEach(function (g, i) {
            groupColor[g] = PALETTE[i % PALETTE.length];
        });

        var categories = groups.map(function (g) {
            return { name: g, itemStyle: { color: groupColor[g] } };
        });
        categories.push({ name: EXTERNAL, itemStyle: { color: '#6fa8dc' } });

        var categoryOf = {};
        groups.forEach(function (g, i) {
            categoryOf[g] = i;
        });
        categoryOf[EXTERNAL] = categories.length - 1;

        var nodes = graph.nodes.map(function (n) {
            var external = n.external === true;
            var d = degree[n.id] || 1;
            var group = external ? EXTERNAL : (n.project ? String(n.project) : NO_PROJECT);
            return {
                id: n.id,
                name: n.name,
                external: external,
                project: external ? '' : group,
                category: categoryOf[group],
                value: d,
                symbolSize: Math.min(34, 7 + Math.sqrt(d) * 5),
                itemStyle: {
                    color: external ? '#6fa8dc' : groupColor[group],
                    borderColor: 'rgba(3,3,3,.8)',
                    borderWidth: 1
                }
            };
        });

        var links = (graph.links || []).map(function (l) {
            return { source: l.source, target: l.target };
        });

        var chart = echarts.init(host, null, { renderer: 'canvas' });
        echartsInstances.push(chart);

        chart.setOption({
            backgroundColor: 'transparent',
            tooltip: Object.assign({
                formatter: function (p) {
                    if (p.dataType === 'edge') {
                        return esc(p.data.source) + ' → ' + esc(p.data.target);
                    }
                    var projectLine = (!p.data.external && p.data.project && p.data.project !== NO_PROJECT)
                        ? '<br/>project: ' + esc(p.data.project)
                        : '';
                    return '<b>' + esc(p.data.name) + '</b><br/>'
                        + (p.data.external ? 'external dependency' : 'hosted package')
                        + projectLine
                        + '<br/>dependencies: ' + p.data.value;
                }
            }, tooltip),
            legend: [{
                data: groups.concat([EXTERNAL]),
                textStyle: { color: TEXT, fontSize: 11 },
                top: 4,
                right: 8,
                itemWidth: 10,
                itemHeight: 10,
                inactiveColor: '#4a4a4a'
            }],
            series: [{
                type: 'graph',
                layout: 'force',
                roam: true,
                draggable: true,
                focusNodeAdjacency: true,
                categories: categories,
                /* the node names are hidden by default so that the network
                   stays readable; hovering a node reveals the name of the
                   focused node and of its one hop neighbours together with the
                   connecting edges (the adjacency focus puts both of them into
                   the emphasis state). */
                label: {
                    show: false,
                    position: 'right',
                    distance: 6,
                    color: TEXT,
                    fontSize: 10.5,
                    textBorderColor: 'rgba(3,3,3,.85)',
                    textBorderWidth: 2
                },
                edgeSymbol: ['none', 'arrow'],
                edgeSymbolSize: [0, 5],
                lineStyle: {
                    color: 'rgba(255,255,255,.16)',
                    width: 1,
                    curveness: 0.08
                },
                emphasis: {
                    focus: 'adjacency',
                    label: {
                        show: true,
                        color: TEXT_STRONG,
                        fontSize: 10.5,
                        fontWeight: 500
                    },
                    itemStyle: {
                        borderColor: TEXT_STRONG,
                        borderWidth: 1
                    },
                    lineStyle: { color: ACCENT, opacity: 1, width: 1.6 }
                },
                blur: {
                    itemStyle: { opacity: 0.25 },
                    label: { show: false },
                    lineStyle: { opacity: 0.08 }
                },
                force: {
                    repulsion: 160,
                    gravity: 0.06,
                    edgeLength: [50, 140],
                    friction: 0.2
                },
                data: nodes,
                links: links
            }]
        });

        chart.on('click', function (params) {
            if (params && params.dataType === 'node' && params.data) {
                window.location.href = packageLink(params.data.id, params.data.external !== true);
            }
        });

        /* the adjacency focus of echarts only emphasises the hovered element
           itself, so the one hop neighbours are highlighted explicitly: that
           reveals the package name label of the focused node together with the
           names of every directly connected package. */
        var adjacency = {};
        var nodeIndex = {};

        nodes.forEach(function (n, i) {
            adjacency[n.id] = [];
            nodeIndex[n.id] = i;
        });

        links.forEach(function (l) {
            if (adjacency[l.source] && adjacency[l.target]) {
                adjacency[l.source].push(l.target);
                adjacency[l.target].push(l.source);
            }
        });

        chart.on('mouseover', function (params) {
            if (!params || params.dataType !== 'node' || !params.data) {
                return;
            }

            var focus = [params.dataIndex];

            (adjacency[params.data.id] || []).forEach(function (id) {
                if (nodeIndex[id] !== undefined) {
                    focus.push(nodeIndex[id]);
                }
            });

            chart.dispatchAction({ type: 'highlight', seriesIndex: 0, dataIndex: focus });
        });

        chart.on('mouseout', function () {
            chart.dispatchAction({ type: 'downplay', seriesIndex: 0 });
        });

        return chart;
    }

    /* ------------------------------ bootstrap ------------------------------ */

    function initGraph() {
        fetchJSON('/api/stats/tags').then(function (tags) {
            var list = (tags && tags.tags) || [];

            /* the whole feed distribution is cached so that the All chip can
               restore it without another request. */
            graphState.allTags = list;
            graphState.totalTags = (tags && tags.totalTags) || list.length;
            graphState.totalPackages = (tags && tags.totalPackages) || 0;

            setText('stat-graph-packages', graphState.totalPackages);
            setText('stat-graph-tags', graphState.totalTags);

            if (!list.length) {
                showEmpty('chart-tag-cloud', 'no tags found');
                showEmpty('chart-tag-bar', 'no tags found');
            } else {
                renderTagCharts(list, 'all packages');
            }
        }).catch(function (error) {
            showEmpty('chart-tag-cloud', 'failed to load tags: ' + error.message);
            showEmpty('chart-tag-bar', 'failed to load tags: ' + error.message);
        });

        /* every cluster chip filters the tag charts; the listener is registered
           once here (the legend is re-rendered, the listener is not). */
        var legend = $('cluster-legend');

        if (legend) {
            legend.addEventListener('click', function (event) {
                var button = event.target;

                while (button && button.nodeType === 1 && button !== legend &&
                    !button.getAttribute('data-cluster')) {
                    button = button.parentNode;
                }

                if (button && button.nodeType === 1 && button !== legend &&
                    button.getAttribute('data-cluster') !== null) {
                    applyClusterFilter(button.getAttribute('data-cluster'));
                }
            });
        }

        showLoading('chart-package-clusters', 'loading cluster scatter…');
        fetchJSON('/api/stats/clusters').then(function (doc) {
            setText('stat-graph-clusters', (doc && doc.k) || 0);
            renderPackageClusters('chart-package-clusters', doc);
        }).catch(function (error) {
            showEmpty('chart-package-clusters', 'failed to load the clusters: ' + error.message);
        });

        showLoading('chart-dependency-graph', 'loading dependency network…');
        fetchJSON('/api/stats/dependency-network').then(function (graph) {
            setText('stat-graph-dep-edges', (graph && graph.links ? graph.links.length : 0));
            renderDependencyNetwork('chart-dependency-graph', graph);
        }).catch(function (error) {
            showEmpty('chart-dependency-graph', 'failed to load dependency network: ' + error.message);
        });

        bindResize();
    }

    document.addEventListener('DOMContentLoaded', function () {
        if (document.body.getAttribute('data-page') === 'graph') {
            initGraph();
        }
    });
})();
