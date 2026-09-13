Imports System.Text

''' <summary>
''' Write the type page of the api reference document site: the type comment
''' text, the member overview tables and the member details which are located
''' by their html anchors.
''' </summary>
Public Class TypePageWriter

    Public Shared Function Render(ctx As DocSiteContext, t As ApiDocType) As String
        Return ctx.Page(t.url, DocHtml.DisplayTypeName(t.name), RenderContent(ctx, t))
    End Function

    ''' <summary>
    ''' render the type page content fragment (without the page shell)
    ''' </summary>
    ''' <param name="ctx"></param>
    ''' <param name="t"></param>
    ''' <param name="includeBreadcrumb">
    ''' renders the built-in breadcrumb line. It is disabled when the breadcrumb
    ''' is provided by the page template (the nuget server side pages).
    ''' </param>
    ''' <returns></returns>
    Public Shared Function RenderContent(ctx As DocSiteContext, t As ApiDocType, Optional includeBreadcrumb As Boolean = True) As String
        Dim md As CommentMarkdown = ctx.Markdown(t.url)
        Dim sb As New StringBuilder

        If includeBreadcrumb Then
            sb.AppendLine($"<p class=""eyebrow""><b>TYPE</b> <span>{ctx.NamespaceBreadcrumb(t.namespaceName, t.url)} / <span class=""crumb on"">{DocHtml.Escape(DocHtml.DisplayTypeName(t.name))}</span></span></p>")
        End If

        sb.AppendLine($"<h1 class=""headline"">{DocHtml.Escape(DocHtml.DisplayTypeName(t.name))}</h1>")

        sb.AppendLine("<div class=""meta"">")
        sb.AppendLine($"<span>Full name <b class=""mono"">{DocHtml.Escape(t.fullName)}</b></span>")
        sb.AppendLine($"<span>Assembly <b>{DocHtml.Escape(t.project)}</b></span>")
        sb.AppendLine($"<span>Members <b>{t.MemberCount()}</b></span>")
        sb.AppendLine("</div>")

        If Not String.IsNullOrWhiteSpace(t.summary) Then
            sb.AppendLine("<div class=""article-body"">")
            sb.AppendLine(md.ToHtml(t.summary))
            sb.AppendLine("</div>")
        End If

        If Not String.IsNullOrWhiteSpace(t.remarks) Then
            sb.AppendLine("<p class=""sec-label""><b>00</b> <span>Remarks</span></p>")
            sb.AppendLine("<div class=""article-body"">")
            sb.AppendLine(md.ToHtml(t.remarks))
            sb.AppendLine("</div>")
        End If

        sb.AppendLine(paramTable(ctx, t.url, md, t.typeParams, "Type Parameters"))

        sb.AppendLine("<p class=""sec-label""><b>01</b> <span>Syntax</span></p>")
        sb.AppendLine($"<div class=""sig ws-pre"">{DocHtml.Escape(t.fullName)}</div>")

        Dim no As Integer = 2

        no = memberSection(sb, ctx, t, "M"c, "Methods", no, md)
        no = memberSection(sb, ctx, t, "P"c, "Properties", no, md)
        no = memberSection(sb, ctx, t, "F"c, "Fields", no, md)
        no = memberSection(sb, ctx, t, "E"c, "Events", no, md)

        sb.AppendLine("<section id=""members"">")
        sb.AppendLine($"<p class=""sec-label""><b>{no.ToString("00")}</b> <span>Members</span></p>")

        If t.members IsNot Nothing Then
            For Each m As ApiDocMember In t.members
                sb.AppendLine(memberBlock(ctx, t, m, md))
            Next
        End If

        sb.AppendLine("</section>")

        Return sb.ToString()
    End Function

    Private Shared Function memberSection(sb As StringBuilder, ctx As DocSiteContext, t As ApiDocType, kind As Char, label As String, no As Integer, md As CommentMarkdown) As Integer
        Dim groups = t.GetMemberGroups(kind).ToList

        If groups.Count = 0 Then
            Return no
        End If

        sb.AppendLine($"<section id=""{label.ToLowerInvariant}"">")
        sb.AppendLine($"<p class=""sec-label""><b>{no.ToString("00")}</b> <span>{DocHtml.Escape(label)}</span></p>")
        sb.AppendLine("<div class=""tablewrap""><table><thead><tr><th>Name</th><th class=""num"">Overloads</th><th>Summary</th></tr></thead><tbody>")

        For Each g In groups
            Dim first As ApiDocMember = g.OrderBy(Function(x) x.overloadIndex).First
            Dim name$ = DocHtml.DisplayTypeName(g.Key)
            Dim summary$ = DocHtml.PlainSummary(first.summary)

            sb.AppendLine("<tr>")
            sb.AppendLine($"<td class=""mono""><a class=""member-link"" href=""#{DocHtml.Attr(first.anchor)}"">{DocHtml.Escape(name)}</a></td>")
            sb.AppendLine($"<td class=""num"">{g.Count}</td>")
            sb.AppendLine($"<td>{DocHtml.Escape(summary)}</td>")
            sb.AppendLine("</tr>")
        Next

        sb.AppendLine("</tbody></table></div>")
        sb.AppendLine("</section>")

        Return no + 1
    End Function

    Private Shared Function memberBlock(ctx As DocSiteContext, t As ApiDocType, m As ApiDocMember, md As CommentMarkdown) As String
        Dim paramTypes$() = DocNaming.ParseParameterTypes(m.declaration)
        Dim sb As New StringBuilder

        sb.AppendLine($"<section class=""member member-{m.kindName}"" id=""{DocHtml.Attr(m.anchor)}"">")
        sb.AppendLine("<div class=""member-head"">")
        sb.AppendLine($"<span class=""badge {m.kindName}"">{m.kindName}</span>")
        sb.AppendLine($"<span class=""member-name"">{DocHtml.Escape(DocHtml.DisplayTypeName(m.name))}</span>")

        If m.overloadIndex > 0 Then
            sb.AppendLine($"<span class=""overload"">overload {m.overloadIndex + 1}</span>")
        End If

        sb.AppendLine($"<a class=""anchor"" href=""#{DocHtml.Attr(m.anchor)}"" title=""link to this member"">#</a>")
        sb.AppendLine("</div>")
        sb.AppendLine($"<div class=""sig"">{ctx.MemberSignature(t, m, t.url)}</div>")

        If Not String.IsNullOrWhiteSpace(m.summary) Then
            sb.AppendLine($"<div class=""member-body"">{md.ToHtml(m.summary)}</div>")
        End If

        If Not String.IsNullOrWhiteSpace(m.remarks) Then
            sb.AppendLine("<div class=""sub-label"">Remarks</div>")
            sb.AppendLine($"<div class=""member-body"">{md.ToHtml(m.remarks)}</div>")
        End If

        sb.AppendLine(paramTable(ctx, t.url, md, m.typeParams, "Type Parameters"))
        sb.AppendLine(paramTable(ctx, t.url, md, m.parameters, "Parameters", paramTypes))

        If Not String.IsNullOrWhiteSpace(m.returns) Then
            sb.AppendLine("<div class=""sub-label"">Returns</div>")
            sb.AppendLine($"<div class=""member-body"">{md.ToHtml(m.returns)}</div>")
        End If

        If Not String.IsNullOrWhiteSpace(m.example) Then
            sb.AppendLine("<div class=""sub-label"">Example</div>")
            sb.AppendLine($"<div class=""member-body example"">{md.ToHtml(m.example)}</div>")
        End If

        sb.AppendLine("</section>")

        Return sb.ToString()
    End Function

    ''' <summary>
    ''' render the parameter table. when the <paramref name="typeRefs"/> is given,
    ''' an extra ``Type`` column with the type page links is rendered.
    ''' </summary>
    ''' <param name="ctx"></param>
    ''' <param name="pageUrl"></param>
    ''' <param name="md"></param>
    ''' <param name="items"></param>
    ''' <param name="title"></param>
    ''' <param name="typeRefs"></param>
    ''' <returns></returns>
    Private Shared Function paramTable(ctx As DocSiteContext, pageUrl As String, md As CommentMarkdown, items As List(Of ApiDocParam), title As String, Optional typeRefs As String() = Nothing) As String
        If items Is Nothing OrElse items.Count = 0 Then
            Return ""
        End If

        Dim hasTypes As Boolean = typeRefs IsNot Nothing AndAlso typeRefs.Length > 0
        Dim sb As New StringBuilder

        sb.AppendLine($"<div class=""sub-label"">{DocHtml.Escape(title)}</div>")

        If hasTypes Then
            sb.AppendLine("<div class=""tablewrap""><table class=""param-table""><thead><tr><th>Name</th><th>Type</th><th>Description</th></tr></thead><tbody>")
        Else
            sb.AppendLine("<div class=""tablewrap""><table class=""param-table""><thead><tr><th>Name</th><th>Description</th></tr></thead><tbody>")
        End If

        For i As Integer = 0 To items.Count - 1
            Dim p As ApiDocParam = items(i)
            Dim typeCell$ = ""

            If hasTypes Then
                Dim typeRef$ = If(i < typeRefs.Length, typeRefs(i), "")
                Dim cell$ = If(typeRef.Length > 0, ctx.RenderTypeReference(typeRef, pageUrl), "")

                typeCell = $"<td class=""ptype"">{cell}</td>"
            End If

            sb.AppendLine($"<tr><td class=""pname""><code>{DocHtml.Escape(p.name)}</code></td>{typeCell}<td class=""pdesc"">{md.ToHtml(p.text)}</td></tr>")
        Next

        sb.AppendLine("</tbody></table></div>")

        Return sb.ToString()
    End Function
End Class
