Imports System.Text
Imports System.Text.RegularExpressions

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

        Dim p = s.IndexOf(". "c)

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
    ''' build the sidebar navigation tree. the types of the active namespace are
    ''' always expanded, the other namespaces are expanded only when the whole
    ''' document site is small enough.
    ''' </summary>
    ''' <param name="pageUrl"></param>
    ''' <returns></returns>
    Public Function Sidebar(pageUrl As String) As String
        Dim base$ = BaseUrl(pageUrl)
        Dim activeNs$ = ActiveNamespace(pageUrl)
        Dim includeAllTypes As Boolean = Site.Types.Count <= 600
        Dim sb As New StringBuilder

        sb.AppendLine("<div class=""doc-side-head"">")
        sb.AppendLine("<input id=""doc-filter"" class=""doc-filter"" type=""search"" placeholder=""filter…"" autocomplete=""off"" />")
        sb.AppendLine("</div>")
        sb.AppendLine("<nav class=""doc-tree"">")

        For Each ns As DocNamespaceEntry In Site.Namespaces
            Dim isActive As Boolean = String.Equals(ns.Name, activeNs, StringComparison.Ordinal)
            Dim isOn As Boolean = String.Equals(ns.Url, pageUrl, StringComparison.OrdinalIgnoreCase)
            Dim open As Boolean = isActive OrElse includeAllTypes

            sb.AppendLine($"<details class=""doc-ns""{(If(open, " open", ""))}>")
            sb.AppendLine($"<summary><a class=""{(If(isOn, "on", ""))}"" href=""{base}{ns.Url}"">{DocHtml.Escape(DisplayNamespace(ns.Name))}</a><span class=""count"">{ns.Types.Count}</span></summary>")

            If open Then
                sb.AppendLine("<ul class=""doc-types"">")

                For Each t As DocTypeEntry In ns.Types
                    Dim on As String = If(String.Equals(t.Url, pageUrl, StringComparison.OrdinalIgnoreCase), " class=""on""", "")
                    sb.AppendLine($"<li><a{on} href=""{base}{t.Url}"">{DocHtml.Escape(DocHtml.DisplayTypeName(t.Name))}</a></li>")
                Next

                sb.AppendLine("</ul>")
            End If

            sb.AppendLine("</details>")
        Next

        sb.AppendLine("</nav>")

        Return sb.ToString
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
                Return t.Namespace.Name
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
