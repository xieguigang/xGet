Imports System.Text
Imports System.Text.RegularExpressions
Imports Microsoft.VisualBasic.ApplicationServices

''' <summary>
''' The low level html helpers of the api reference document site.
''' </summary>
Public Module DocHtml

    ''' <summary>
    ''' escape the text for a html text node context
    ''' </summary>
    ''' <param name="text"></param>
    ''' <returns></returns>
    Public Function Escape(text As String) As String
        If text Is Nothing Then
            Return ""
        End If

        Return text _
            .Replace("&", "&amp;") _
            .Replace("<", "&lt;") _
            .Replace(">", "&gt;")
    End Function

    ''' <summary>
    ''' escape the text for a html attribute context
    ''' </summary>
    ''' <param name="text"></param>
    ''' <returns></returns>
    Public Function Attr(text As String) As String
        If text Is Nothing Then
            Return ""
        End If

        Return Escape(text) _
            .Replace("""", "&quot;") _
            .Replace("'", "&#39;")
    End Function

    ''' <summary>
    ''' strip the markdown markers and take the first sentence of a comment text,
    ''' it is used in the summary columns of the overview tables.
    ''' </summary>
    ''' <param name="markdown"></param>
    ''' <param name="maxLen"></param>
    ''' <returns></returns>
    Public Function PlainSummary(markdown As String, Optional maxLen As Integer = 180) As String
        If String.IsNullOrWhiteSpace(markdown) Then
            Return ""
        End If

        Dim s$ = markdown _
            .Replace(vbCrLf, " ") _
            .Replace(vbCr, " ") _
            .Replace(vbLf, " ")

        ' markdown link -> its display text
        s = Regex.Replace(s, "\[([^\]]*)\]\([^)]*\)", "$1")

        ' code fence / inline code markers
        s = s.Replace("`", "").Replace("**", "").Replace("__", "")
        s = s.Trim(" "c, "#"c, "*"c, "-"c, ">"c, "|"c, vbTab)

        Dim p = s.IndexOf(". ", StringComparison.Ordinal)

        If p > 20 Then
            s = s.Substring(0, p + 1)
        End If

        If s.Length > maxLen Then
            s = s.Substring(0, maxLen).TrimEnd & "…"
        End If

        Return s
    End Function

    ''' <summary>
    ''' remove the generic arity suffix of a clr type name
    ''' </summary>
    ''' <param name="name"></param>
    ''' <returns></returns>
    Public Function DisplayTypeName(name As String) As String
        If String.IsNullOrEmpty(name) Then
            Return ""
        End If

        Dim p = name.IndexOf("`"c)

        If p > 0 Then
            name = name.Substring(0, p)
        End If

        If name.Contains("#") Then
            name = name.Replace("#", ".")
        End If

        Return name
    End Function
End Module

''' <summary>
''' The shared page rendering context of the api reference document site.
''' </summary>
Public Class DocSiteContext

    Public Property Site As ApiDocSite
    Public Property Options As ApiDocOptions
    Public Property Theme As ThemeBundle

    ''' <summary>
    ''' the site title, example as ``API Reference``
    ''' </summary>
    ''' <returns></returns>
    Public ReadOnly Property Title As String
        Get
            Return If(Options Is Nothing, "API Reference", Options.Title)
        End Get
    End Property

    ''' <summary>
    ''' the relative url prefix that jumps from the given page back to the site root
    ''' </summary>
    ''' <param name="pageUrl"></param>
    ''' <returns></returns>
    Public Function BaseUrl(pageUrl As String) As String
        Dim depth As Integer = 0

        If Not String.IsNullOrEmpty(pageUrl) Then
            For Each c As Char In pageUrl
                If c = "/"c Then
                    depth += 1
                End If
            Next
        End If

        Dim sb As New StringBuilder

        For i As Integer = 1 To depth
            sb.Append("../")
        Next

        Return sb.ToString
    End Function

    Public Function Markdown(pageUrl As String) As CommentMarkdown
        Return New CommentMarkdown(Site, BaseUrl(pageUrl))
    End Function

    ''' <summary>
    ''' build the namespace breadcrumb which is split by the namespace segments.
    ''' every segment links to its namespace page when that namespace exists in
    ''' the current document site, otherwise it is rendered as a plain text.
    ''' </summary>
    ''' <param name="namespaceName"></param>
    ''' <param name="pageUrl"></param>
    ''' <param name="lastIsText">renders the last segment as the current page text</param>
    ''' <returns></returns>
    Public Function NamespaceBreadcrumb(namespaceName As String, pageUrl As String, Optional lastIsText As Boolean = False) As String
        Dim base$ = BaseUrl(pageUrl)
        Dim sb As New StringBuilder

        sb.Append($"<a href=""{base}index.html"">Overview</a>")

        If String.IsNullOrWhiteSpace(namespaceName) Then
            sb.Append($" / <span class=""crumb{(If(lastIsText, " on", ""))}"">(global)</span>")
            Return sb.ToString
        End If

        Dim segments = namespaceName _
            .Split("."c) _
            .Where(Function(s) s.Length > 0) _
            .ToArray
        Dim path$ = ""

        For i As Integer = 0 To segments.Length - 1
            Dim segment$ = segments(i)
            path = If(path.Length = 0, segment, path & "." & segment)

            If lastIsText AndAlso i = segments.Length - 1 Then
                sb.Append($" / <span class=""crumb on"">{DocHtml.Escape(segment)}</span>")
                Continue For
            End If

            Dim entry As DocNamespaceEntry = Nothing

            If Site.NamespaceByName.TryGetValue(path, entry) Then
                sb.Append($" / <a href=""{base}{entry.Url}"">{DocHtml.Escape(segment)}</a>")
            Else
                sb.Append($" / <span class=""crumb"">{DocHtml.Escape(segment)}</span>")
            End If
        Next

        Return sb.ToString
    End Function

    ''' <summary>
    ''' render the whole html page with the shared header, sidebar tree and footer
    ''' </summary>
    ''' <param name="pageUrl">the site relative url of the current page</param>
    ''' <param name="title"></param>
    ''' <param name="content"></param>
    ''' <returns></returns>
    Public Function Page(pageUrl As String, title As String, content As String) As String
        Dim base$ = BaseUrl(pageUrl)
        Dim sb As New StringBuilder

        sb.AppendLine("<!DOCTYPE html>")
        sb.AppendLine("<html lang=""en"">")
        sb.AppendLine("<head>")
        sb.AppendLine("<meta charset=""UTF-8"" />")
        sb.AppendLine("<meta name=""viewport"" content=""width=device-width, initial-scale=1.0"" />")
        sb.AppendLine($"<title>{DocHtml.Escape(title)} · {DocHtml.Escape(Title)}</title>")
        sb.AppendLine($"<link rel=""icon"" type=""image/png"" href=""{base}favicon.png"" />")

        For Each css As String In Theme.Css
            sb.AppendLine($"<link rel=""stylesheet"" href=""{base}{css}"" />")
        Next

        sb.AppendLine("</head>")
        sb.AppendLine("<body data-page=""doc"">")
        sb.AppendLine("<a id=""top""></a>")

        ' --- top navigation ---
        sb.AppendLine("<header class=""topbar"">")
        sb.AppendLine($"<a class=""brand"" href=""{base}index.html"">")
        sb.AppendLine($"<img src=""{base}favicon.png"" alt=""logo"" onerror=""this.style.display='none'"" />")
        sb.AppendLine($"<strong>{DocHtml.Escape(Title)}</strong>")
        sb.AppendLine($"<span class=""sub"">{DocHtml.Escape(Options.SubTitle)}</span>")
        sb.AppendLine("</a>")
        sb.AppendLine("<nav class=""topnav"">")
        sb.AppendLine($"<a{(If(String.Equals(pageUrl, "index.html", StringComparison.OrdinalIgnoreCase), " class=""on""", ""))} href=""{base}index.html"">Overview</a>")
        sb.AppendLine($"<a href=""{base}index.html#namespaces"">Namespaces</a>")
        sb.AppendLine($"<a href=""{base}index.html#assemblies"">Assemblies</a>")
        sb.AppendLine("</nav>")
        sb.AppendLine("<a class=""home-btn"" href=""#top"" title=""Top"" aria-label=""Top"">↑</a>")
        sb.AppendLine("</header>")

        ' --- body shell ---
        sb.AppendLine("<div class=""doc-shell"">")
        sb.AppendLine("<aside class=""doc-side"">")
        sb.AppendLine(Sidebar(pageUrl))
        sb.AppendLine("</aside>")
        sb.AppendLine("<main class=""doc-main"">")
        sb.AppendLine("<div class=""wrap doc-wrap"">")
        sb.AppendLine(content)
        sb.AppendLine("</div>")
        sb.AppendLine("</main>")
        sb.AppendLine("</div>")

        ' --- footer ---
        sb.AppendLine("<footer class=""site"">")
        sb.AppendLine($"<div class=""legal"">generated by Readership · {DocHtml.Escape(Theme.Name)} theme · {Date.Now.ToString("yyyy-MM-dd")}</div>")
        sb.AppendLine($"<div class=""legal"">{Site.Namespaces.Count} namespaces · {Site.Types.Count} types · {Site.MemberCount} members</div>")
        sb.AppendLine("</footer>")

        For Each js As String In Theme.Js
            sb.AppendLine($"<script src=""{base}{js}""></script>")
        Next

        sb.AppendLine("</body>")
        sb.AppendLine("</html>")

        Return sb.ToString
    End Function

    ''' <summary>
    ''' build the sidebar navigation tree from the namespace tree. every node
    ''' only displays its own segment name, and the ancestor chain of the
    ''' namespace that the current page belongs to is expanded automatically.
    ''' </summary>
    ''' <param name="pageUrl"></param>
    ''' <returns></returns>
    Public Function Sidebar(pageUrl As String) As String
        Dim base$ = BaseUrl(pageUrl)
        Dim activeNs$ = ActiveNamespace(pageUrl)
        Dim tree As FileSystemTree = Site.NamespaceTree
        Dim sb As New StringBuilder

        sb.AppendLine("<div class=""doc-side-head"">")
        sb.AppendLine("<input id=""doc-filter"" class=""doc-filter"" type=""search"" placeholder=""filter…"" autocomplete=""off"" />")
        sb.AppendLine("</div>")
        sb.AppendLine("<nav class=""doc-tree"" id=""doc-tree"">")

        If tree Is Nothing OrElse tree.Files Is Nothing OrElse tree.Files.Count = 0 Then
            sb.AppendLine(SidebarGlobal(pageUrl, base))
        Else
            sb.AppendLine(SidebarGlobal(pageUrl, base))
            sb.AppendLine("<ul class=""doc-children doc-root"">")

            For Each node As FileSystemTree In tree.Files.Values.OrderBy(Function(n) n.Name)
                sb.AppendLine("<li>")
                sb.AppendLine(RenderNamespaceNode(node, pageUrl, base, activeNs))
                sb.AppendLine("</li>")
            Next

            sb.AppendLine("</ul>")
        End If

        sb.AppendLine("</nav>")

        Return sb.ToString
    End Function

    ''' <summary>
    ''' render the global namespace (the namespace name is empty) as the first
    ''' level node of the sidebar tree
    ''' </summary>
    ''' <param name="pageUrl"></param>
    ''' <param name="base"></param>
    ''' <returns></returns>
    Private Function SidebarGlobal(pageUrl As String, base As String) As String
        Dim entry As DocNamespaceEntry = Nothing

        If Not Site.NamespaceByName.TryGetValue("", entry) Then
            Return ""
        End If

        Dim onAttr$ = If(String.Equals(entry.Url, pageUrl, StringComparison.OrdinalIgnoreCase), " class=""on""", "")
        Dim sb As New StringBuilder

        sb.AppendLine("<div class=""doc-global"">")
        sb.AppendLine($"<a{onAttr} href=""{base}{entry.Url}"">(global)</a>")
        sb.AppendLine($"<span class=""count"">{entry.Types.Count}</span>")
        sb.AppendLine("</div>")

        Return sb.ToString
    End Function

    ''' <summary>
    ''' recursively render one node of the namespace tree. the node is rendered
    ''' as a link when it is a real namespace of the document site, otherwise it
    ''' is rendered as a disabled group label.
    ''' </summary>
    ''' <param name="node"></param>
    ''' <param name="pageUrl"></param>
    ''' <param name="base"></param>
    ''' <param name="activeNs"></param>
    ''' <returns></returns>
    Private Function RenderNamespaceNode(node As FileSystemTree, pageUrl As String, base As String, activeNs As String) As String
        Dim fullName$ = ApiDocSite.NodeFullName(node)
        Dim entry As DocNamespaceEntry = Nothing
        Dim hasEntry As Boolean = Site.NamespaceByName.TryGetValue(fullName, entry)
        Dim children = node.Files
        Dim hasChildren As Boolean = children IsNot Nothing AndAlso children.Count > 0
        Dim isActive As Boolean = String.Equals(fullName, activeNs, StringComparison.Ordinal)
        Dim isExpanded As Boolean = isActive OrElse (hasChildren AndAlso isAncestor(activeNs, fullName))
        Dim isLeaf As Boolean = Not hasChildren AndAlso Not isActive
        Dim sb As New StringBuilder

        sb.AppendLine($"<details class=""doc-ns{(If(isActive, " active", ""))}{(If(isLeaf, " leaf", ""))}""{(If(isExpanded, " open", ""))}{(If(isExpanded, " data-default=""1""", ""))}>")
        sb.AppendLine("<summary>")

        If hasEntry Then
            Dim onAttr$ = If(String.Equals(entry.Url, pageUrl, StringComparison.OrdinalIgnoreCase), " class=""on""", "")
            sb.AppendLine($"<a{onAttr} href=""{base}{entry.Url}"" title=""{DocHtml.Attr(fullName)}"">{DocHtml.Escape(node.Name)}</a>")
        Else
            sb.AppendLine($"<span class=""doc-node"" title=""{DocHtml.Attr(fullName)}"">{DocHtml.Escape(node.Name)}</span>")
        End If

        If hasEntry AndAlso entry.Types.Count > 0 Then
            sb.AppendLine($"<span class=""count"">{entry.Types.Count}</span>")
        End If

        sb.AppendLine("</summary>")

        ' the types of the current namespace are listed as the leaf nodes, the
        ' other namespaces only list their child namespaces to keep the sidebar
        ' narrow enough.
        If isActive AndAlso hasEntry AndAlso entry.Types.Count > 0 Then
            sb.AppendLine("<ul class=""doc-types"">")

            For Each t As DocTypeEntry In entry.Types
                Dim onAttr$ = If(String.Equals(t.Url, pageUrl, StringComparison.OrdinalIgnoreCase), " class=""on""", "")
                sb.AppendLine($"<li><a{onAttr} href=""{base}{t.Url}"">{DocHtml.Escape(DocHtml.DisplayTypeName(t.Name))}</a></li>")
            Next

            sb.AppendLine("</ul>")
        End If

        If hasChildren Then
            sb.AppendLine("<ul class=""doc-children"">")

            For Each child As FileSystemTree In children.Values.OrderBy(Function(n) n.Name)
                sb.AppendLine("<li>")
                sb.AppendLine(RenderNamespaceNode(child, pageUrl, base, activeNs))
                sb.AppendLine("</li>")
            Next

            sb.AppendLine("</ul>")
        End If

        sb.AppendLine("</details>")

        Return sb.ToString
    End Function

    ''' <summary>
    ''' is the given <paramref name="nodeName"/> an ancestor namespace of the
    ''' <paramref name="activeNs"/> namespace
    ''' </summary>
    ''' <param name="activeNs"></param>
    ''' <param name="nodeName"></param>
    ''' <returns></returns>
    Private Shared Function isAncestor(activeNs As String, nodeName As String) As Boolean
        If String.IsNullOrEmpty(activeNs) OrElse String.IsNullOrEmpty(nodeName) Then
            Return False
        End If

        Return activeNs.StartsWith(nodeName & ".", StringComparison.Ordinal)
    End Function

    ''' <summary>
    ''' find the namespace that the given page belongs to
    ''' </summary>
    ''' <param name="pageUrl"></param>
    ''' <returns></returns>
    Public Function ActiveNamespace(pageUrl As String) As String
        If String.IsNullOrEmpty(pageUrl) Then
            Return Nothing
        End If

        For Each ns As DocNamespaceEntry In Site.Namespaces
            If String.Equals(ns.Url, pageUrl, StringComparison.OrdinalIgnoreCase) Then
                Return ns.Name
            End If
        Next

        For Each t As DocTypeEntry In Site.Types
            If String.Equals(t.Url, pageUrl, StringComparison.OrdinalIgnoreCase) Then
                Return t.ContainingNamespace.Name
            End If
        Next

        Return Nothing
    End Function

    ''' <summary>
    ''' the display text of a namespace name, the empty name is the global namespace
    ''' </summary>
    ''' <param name="name"></param>
    ''' <returns></returns>
    Public Shared Function DisplayNamespace(name As String) As String
        If String.IsNullOrWhiteSpace(name) Then
            Return "(global)"
        End If

        Return name
    End Function
End Class
