<?xml version="1.0" encoding="UTF-8"?>
<!--
    sitemap.xsl — browser rendering of the generated /sitemap.xml.

    the style sheet is applied by the browser through the xml-stylesheet
    processing instruction of the generated document. it reuses the very same
    design tokens of the site (assets/css/scibasic.css) so that the site map
    looks like a regular content page of the feed.
-->
<xsl:stylesheet version="1.0"
                xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                xmlns:s="http://www.sitemaps.org/schemas/sitemap/0.9"
                exclude-result-prefixes="s">

    <xsl:output method="html" encoding="UTF-8" indent="yes" />
    <xsl:strip-space elements="*" />

    <xsl:template match="/">
        <html lang="en">

        <head>
            <meta charset="UTF-8" />
            <meta name="viewport" content="width=device-width, initial-scale=1.0" />
            <title>Site map · nuget</title>
            <link rel="icon" type="image/png" href="/favicon.png" />
            <link rel="stylesheet" href="/assets/css/scibasic.css" />
            <style>
                /* ---- sitemap page: only the structures which are not part of
                        the site sheet are defined here, everything else is
                        inherited from scibasic.css ---- */
                .smap-legend {
                    display: flex;
                    flex-wrap: wrap;
                    gap: 18px;
                    margin: 0 0 18px;
                }

                .smap-legend span {
                    font-size: 11px;
                    letter-spacing: .16em;
                    text-transform: uppercase;
                    color: var(--ink-ghost);
                }

                .smap-badge {
                    display: inline-block;
                    padding: 2px 9px;
                    border: 1px solid var(--hairline);
                    border-radius: 999px;
                    font-size: 10px;
                    letter-spacing: .14em;
                    text-transform: uppercase;
                    color: var(--ink-faint);
                    white-space: nowrap;
                }

                .smap-badge.docs {
                    color: var(--green);
                    border-color: rgba(63, 174, 74, .42);
                }

                .smap-badge.package {
                    color: var(--blue);
                    border-color: rgba(111, 168, 220, .34);
                }

                td.smap-url {
                    max-width: 62ch;
                    overflow: hidden;
                    text-overflow: ellipsis;
                    white-space: nowrap;
                }

                td.smap-url a {
                    font-family: var(--mono);
                    font-size: 12.5px;
                    color: var(--ink-dim);
                    transition: color .2s var(--ease);
                }

                td.smap-url a:hover {
                    color: var(--green);
                    text-decoration: underline;
                    text-underline-offset: 4px;
                }

                td.smap-time {
                    font-family: var(--mono);
                    font-size: 12px;
                    color: var(--ink-faint);
                    white-space: nowrap;
                }

                .smap-foot {
                    margin-top: 30px;
                }
            </style>
        </head>

        <body data-page="sitemap">

            <header class="topbar">
                <a class="brand" href="/index.html">
                    <img src="/favicon.png" alt="nuget server logo" onerror="this.style.display='none'" />
                    <strong>nuget</strong>
                    <span class="sub">package server</span>
                </a>
                <nav class="topnav">
                    <a href="/index.html">Packages</a>
                    <a href="/about.html">Statistics</a>
                    <a href="/graph.html">Graphs</a>
                    <a href="/docs/index.html">API Docs</a>
                    <a href="/v3/index.json">Service Index</a>
                </nav>
                <a class="home-btn" href="/index.html" title="Home" aria-label="Home">↖</a>
            </header>

            <main class="wrap">
                <p class="eyebrow"><b>00</b> <span>Index — Site map</span></p>
                <h1 class="headline">Every <span class="u">page</span> of the feed.</h1>

                <p class="lede">
                    The site map lists the static pages of the web front end, the detail page of every
                    published package and every server side rendered api document page. It is rebuilt in
                    the background whenever the document database changed and the server is idle.
                </p>

                <div class="meta">
                    <span>Urls <b><xsl:value-of select="count(s:urlset/s:url)" /></b></span>
                    <span>Pages <b><xsl:value-of select="count(s:urlset/s:url[not(contains(s:loc, '/docs/')) and not(contains(s:loc, 'package.html'))])" /></b></span>
                    <span>Packages <b><xsl:value-of select="count(s:urlset/s:url[contains(s:loc, 'package.html')])" /></b></span>
                    <span>Api docs <b><xsl:value-of select="count(s:urlset/s:url[contains(s:loc, '/docs/')])" /></b></span>
                    <span>Latest update
                        <b>
                            <xsl:for-each select="s:urlset/s:url[s:lastmod]">
                                <xsl:sort select="s:lastmod" order="descending" />
                                <xsl:if test="position() = 1">
                                    <xsl:value-of select="s:lastmod" />
                                </xsl:if>
                            </xsl:for-each>
                        </b>
                    </span>
                </div>

                <section id="urls">
                    <p class="sec-label"><b>01</b> <span>Url set</span></p>

                    <div class="smap-legend">
                        <span>page — static page of the web front end</span>
                        <span>package — package detail page</span>
                        <span>docs — api document page</span>
                    </div>

                    <div class="tablewrap">
                        <table>
                            <thead>
                                <tr>
                                    <th>Url</th>
                                    <th>Last modified</th>
                                    <th>Kind</th>
                                </tr>
                            </thead>
                            <tbody>
                                <xsl:apply-templates select="s:urlset/s:url" />
                            </tbody>
                        </table>
                    </div>

                    <div class="note smap-foot">
                        <b>Sitemap</b> is generated as a plain xml document
                        (<span class="mono">/sitemap.xml</span>) and is styled in the browser by
                        <span class="mono">/assets/sitemap.xsl</span>. Search engines read the raw
                        xml, the style sheet only affects the human readable rendering.
                    </div>
                </section>
            </main>

            <footer class="site">
                <div class="pn">
                    <a href="/index.html"><span>Previous</span>Packages</a>
                    <a href="/docs/index.html"><span>Next</span>API Docs</a>
                </div>
                <div class="legal"><a href="https://github.com/xieguigang/xGet">nuget · experimental feed</a> · powered by <a
                        href="https://github.com/xieguigang/flute">Fluteway</a> + <a
                        href="https://github.com/xieguigang/jsQL">JSql</a></div>
            </footer>

        </body>

        </html>
    </xsl:template>

    <!-- one row per url: the visible text is the site relative path, the
         hyper link target is the absolute url of the record. -->
    <xsl:template match="s:url">
        <xsl:variable name="loc" select="string(s:loc)" />
        <xsl:variable name="kind">
            <xsl:choose>
                <xsl:when test="contains($loc, '/docs/')">docs</xsl:when>
                <xsl:when test="contains($loc, 'package.html')">package</xsl:when>
                <xsl:otherwise>page</xsl:otherwise>
            </xsl:choose>
        </xsl:variable>

        <tr>
            <td class="smap-url">
                <a href="{$loc}" title="{$loc}">
                    <xsl:value-of select="concat('/', substring-after(substring-after($loc, '://'), '/'))" />
                </a>
            </td>
            <td class="smap-time">
                <xsl:value-of select="s:lastmod" />
            </td>
            <td>
                <span class="smap-badge {$kind}">
                    <xsl:value-of select="$kind" />
                </span>
            </td>
        </tr>
    </xsl:template>

</xsl:stylesheet>
