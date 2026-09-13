Imports System.Text.RegularExpressions
Imports Microsoft.VisualBasic.MIME.text.markdown

''' <summary>
''' Render the markdown text of a xml comment document into html, and resolve
''' the ``cref`` cross reference links into the page urls of the document site.
''' 
''' The comment texts are converted into markdown by the xml document
''' preprocessor (see the ``TrimAssemblyDoc`` function of the base code library),
''' then this class renders them with the experimental markdown renderer of the
''' ``markdown.NET5`` project.
''' </summary>
Public Class CommentMarkdown

    Private ReadOnly site As ApiDocSite
    Private ReadOnly baseUrl As String
    Private ReadOnly render As New MarkdownRender()
    Private ReadOnly cache As New Dictionary(Of String, String)

    ''' <summary>
    ''' the markdown renderer emits ``&lt;a href="cref:..."&gt;...&lt;/a&gt;`` for
    ''' the cross reference links, here we resolve the ``cref:`` url into the real
    ''' page url of the document site.
    ''' </summary>
    Private Shared ReadOnly crefAnchor As New Regex(
        "<a\s+href=""cref:(?<id>[^""]*)""[^>]*>(?<text>.*?)</a>",
        RegexOptions.Singleline Or RegexOptions.IgnoreCase)

    Public Sub New(site As ApiDocSite, baseUrl As String)
        Me.site = site
        Me.baseUrl = If(baseUrl, "")
    End Sub

    ''' <summary>
    ''' render the markdown comment text into html
    ''' </summary>
    ''' <param name="text"></param>
    ''' <returns></returns>
    Public Function ToHtml(text As String) As String
        If String.IsNullOrWhiteSpace(text) Then
            Return ""
        End If

        Dim html As String

        If cache.TryGetValue(text, html) Then
            Return html
        End If

        html = render.Transform(text)
        html = crefAnchor.Replace(html, AddressOf resolveCref)

        cache(text) = html

        Return html
    End Function

    Private Function resolveCref(m As Match) As String
        Dim id As String = m.Groups("id").Value
        Dim text As String = m.Groups("text").Value
        Dim url As String = site.ResolveUrl(id)

        If String.IsNullOrEmpty(url) Then
            ' the target is not a part of the current document site, so it is
            ' rendered as a plain inline code text instead of a broken link.
            Return $"<code class=""cref"" title=""{DocHtml.Attr(id)}"">{text}</code>"
        End If

        Return $"<a class=""cref"" href=""{DocHtml.Attr(baseUrl & url)}"">{text}</a>"
    End Function
End Class
