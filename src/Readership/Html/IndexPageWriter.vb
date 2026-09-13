Imports System.Text

''' <summary>
''' Write the index page of the api reference document site: the statistics
''' cards, the namespace cards and the assembly list.
''' </summary>
Public Class IndexPageWriter

    Public Shared Function Render(ctx As DocSiteContext) As String
        Dim site As ApiDocSite = ctx.Site
        Dim sb As New StringBuilder

        sb.AppendLine("<p class=""eyebrow""><b>01</b> <span>API Reference</span></p>")
        sb.AppendLine($"<h1 class=""headline"">The <span class=""u"">api reference</span> of <span class=""accent"">{DocHtml.Escape(ctx.Title)}</span>.</h1>")
        sb.AppendLine($"<p class=""lede"">{DocHtml.Escape(ctx.Options.Description)}</p>")

        ' ---- statistics ----
        sb.AppendLine("<section id=""overview"">")
        sb.AppendLine("<p class=""sec-label""><b>02</b> <span>Statistics</span></p>")
        sb.AppendLine("<div class=""stats"">")
        sb.AppendLine(statCard("01", site.AssemblyNames.Length, "Assemblies"))
        sb.AppendLine(statCard("02", site.Namespaces.Count, "Namespaces"))
        sb.AppendLine(statCard("03", site.Types.Count, "Types"))
        sb.AppendLine(statCard("04", site.MemberCount, "Members"))
        sb.AppendLine("</div>")
        sb.AppendLine("</section>")

        ' ---- namespaces ----
        sb.AppendLine("<section id=""namespaces"">")
        sb.AppendLine("<p class=""sec-label""><b>03</b> <span>Namespaces</span></p>")
        sb.AppendLine("<div class=""toolbar"">")
        sb.AppendLine("<div class=""search"">")
        sb.AppendLine("<svg viewBox=""0 0 24 24"" fill=""none"" stroke-width=""2"" stroke-linecap=""round""><circle cx=""11"" cy=""11"" r=""7""></circle><line x1=""16.5"" y1=""16.5"" x2=""21"" y2=""21""></line></svg>")
        sb.AppendLine("<input id=""index-filter"" type=""search"" placeholder=""filter namespaces…"" autocomplete=""off"" />")
        sb.AppendLine("</div>")
        sb.AppendLine("<div class=""pager""><span id=""index-count""></span></div>")
        sb.AppendLine("</div>")
        sb.AppendLine("<div class=""ns-grid"" id=""ns-grid"">")

        For Each ns As DocNamespaceEntry In site.Namespaces
            Dim summary$ = DocHtml.PlainSummary(ns.Summary)

            sb.AppendLine($"<a class=""ns-card"" data-name=""{DocHtml.Attr(ns.Name)}"" href=""{ns.Url}"">")
            sb.AppendLine($"<div class=""ns-name"">{DocHtml.Escape(DocSiteContext.DisplayNamespace(ns.Name))}</div>")
            sb.AppendLine($"<div class=""ns-meta"">{ns.Types.Count} types · {DocHtml.Escape(ns.Project)}</div>")

            If Not String.IsNullOrEmpty(summary) Then
                sb.AppendLine($"<div class=""ns-sum"">{DocHtml.Escape(summary)}</div>")
            End If

            sb.AppendLine("</a>")
        Next

        sb.AppendLine("</div>")
        sb.AppendLine("</section>")

        ' ---- assemblies ----
        sb.AppendLine("<section id=""assemblies"">")
        sb.AppendLine("<p class=""sec-label""><b>04</b> <span>Assemblies</span></p>")
        sb.AppendLine("<div class=""tablewrap""><table><thead><tr><th>Assembly</th><th class=""num"">Namespaces</th><th class=""num"">Types</th><th class=""num"">Members</th></tr></thead><tbody>")

        For Each g In site.Types _
            .GroupBy(Function(t) t.Project) _
            .OrderBy(Function(x) x.Key)

            Dim nsCount As Integer = site.Namespaces.Where(Function(n) String.Equals(n.Project, g.Key, StringComparison.Ordinal)).Count()
            Dim memberCount As Integer = g.Sum(Function(t) t.Members.Count)

            sb.AppendLine($"<tr><td><span class=""pkg-name"">{DocHtml.Escape(g.Key)}</span></td><td class=""num"">{nsCount}</td><td class=""num"">{g.Count}</td><td class=""num"">{memberCount}</td></tr>")
        Next

        sb.AppendLine("</tbody></table></div>")
        sb.AppendLine("</section>")

        Return ctx.Page("index.html", "Overview", sb.ToString)
    End Function

    Private Shared Function statCard(no As String, value As Integer, label As String) As String
        Return $"<div class=""stat""><div class=""no"">{no}</div><div class=""value"">{value}</div><div class=""label"">{DocHtml.Escape(label)}</div></div>"
    End Function
End Class
