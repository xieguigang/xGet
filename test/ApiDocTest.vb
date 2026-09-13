Imports System.IO
Imports System.Text.RegularExpressions
Imports Readership

''' <summary>
''' The command line test of the <see cref="ApiDoc"/> api reference document
''' generator. It generates a document site from a folder of xml comment
''' documents (or from a single document file), then it validates that all of
''' the generated pages exist and that all of the internal links / anchors could
''' be resolved.
''' 
''' usage: test apidoc [input] [output] [theme]
''' </summary>
Module ApiDocTest

    Public Function Run(args As String()) As Integer
        Dim input$ = If(args.Length > 0 AndAlso Not String.IsNullOrWhiteSpace(args(0)), args(0), "G:\xDoc\dist\bin")
        Dim output$ = If(args.Length > 1 AndAlso Not String.IsNullOrWhiteSpace(args(1)), args(1), "G:\xDoc\dist\docs")
        Dim theme$ = If(args.Length > 2 AndAlso Not String.IsNullOrWhiteSpace(args(2)), args(2), ThemeAssets.DefaultTheme)

        Call Console.WriteLine($"input : {input}")
        Call Console.WriteLine($"output: {output}")
        Call Console.WriteLine($"theme : {theme}")
        Call Console.WriteLine()

        Dim result As DocBuildResult

        Try
            result = ApiDoc.Generate(New ApiDocOptions With {
                .Input = input,
                .Output = output,
                .Theme = theme,
                .Title = "xDoc API Reference"
            })
        Catch ex As Exception
            Call Console.WriteLine($"generation failed: {ex.Message}")
            Return 2
        End Try

        Call Console.WriteLine($"generated : {result}")
        Call Console.WriteLine($"assemblies: {String.Join(", ", result.Assemblies)}")

        For Each w As String In result.Warnings
            Call Console.WriteLine($"  warning: {w}")
        Next

        If result.PageCount = 0 Then
            Call Console.WriteLine("no page was generated.")
            Return 2
        End If

        Dim ok As Boolean = validate(output)

        Call Console.WriteLine()
        Call Console.WriteLine($"validation: {If(ok, "PASS", "FAIL")}")

        Return If(ok, 0, 1)
    End Function

    Private Function validate(output As String) As Boolean
        Dim root$ = Path.GetFullPath(output)
        Dim indexPath$ = Path.Combine(root, "index.html")

        If Not IO.File.Exists(indexPath) Then
            Call Console.WriteLine("  index.html was not generated.")
            Return False
        End If

        Dim htmlPages = Directory.GetFiles(root, "*.html", SearchOption.AllDirectories)
        Dim hrefRegex As New Regex("<a\s[^>]*?href=""(?<u>[^""]*)""", RegexOptions.IgnoreCase)
        Dim idRegex As New Regex("id=""(?<id>[^""]*)""", RegexOptions.IgnoreCase)
        Dim idCache As New Dictionary(Of String, HashSet(Of String))(StringComparer.OrdinalIgnoreCase)
        Dim problems As New List(Of String)
        Dim crefResolved As Integer = 0
        Dim crefExternal As Integer = 0

        Call Console.WriteLine($"  html pages: {htmlPages.Length}")

        For Each htmlFile As String In htmlPages
            Dim html$ = IO.File.ReadAllText(htmlFile)
            Dim pageDir$ = Path.GetDirectoryName(htmlFile)

            If html.Contains("href=""cref:") Then
                Call problems.Add($"{relative(root, htmlFile)}: the cref url was not resolved")
            End If

            crefResolved += Regex.Matches(html, "<a class=""cref""").Count
            crefExternal += Regex.Matches(html, "<code class=""cref""").Count

            For Each m As Match In hrefRegex.Matches(html)
                Dim href$ = m.Groups("u").Value

                If String.IsNullOrEmpty(href) OrElse
                   href.StartsWith("http", StringComparison.OrdinalIgnoreCase) OrElse
                   href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) Then

                    Continue For
                End If

                Dim hashPos = href.IndexOf("#"c)
                Dim linkPath$ = If(hashPos >= 0, href.Substring(0, hashPos), href)
                Dim fragment$ = If(hashPos >= 0, href.Substring(hashPos + 1), "")

                If String.IsNullOrEmpty(linkPath) Then
                    If Not String.IsNullOrEmpty(fragment) AndAlso
                       Not getIds(idCache, htmlFile, idRegex).Contains(fragment) Then

                        Call problems.Add($"{relative(root, htmlFile)}: broken anchor #{fragment}")
                    End If

                    Continue For
                End If

                Dim targetFile$ = Path.GetFullPath(Path.Combine(pageDir, linkPath.Replace("/"c, Path.DirectorySeparatorChar)))

                If Not IO.File.Exists(targetFile) Then
                    Call problems.Add($"{relative(root, htmlFile)}: broken link -> {linkPath}")
                ElseIf Not String.IsNullOrEmpty(fragment) AndAlso
                       Not getIds(idCache, targetFile, idRegex).Contains(fragment) Then

                    Call problems.Add($"{relative(root, htmlFile)}: broken anchor -> {linkPath}#{fragment}")
                End If
            Next
        Next

        Call Console.WriteLine($"  cref links: {crefResolved} resolved, {crefExternal} external")
        Call Console.WriteLine($"  problems  : {problems.Count}")

        For i As Integer = 0 To Math.Min(problems.Count, 30) - 1
            Call Console.WriteLine($"    - {problems(i)}")
        Next

        Return problems.Count = 0
    End Function

    Private Function getIds(cache As Dictionary(Of String, HashSet(Of String)), htmlPath As String, idRegex As Regex) As HashSet(Of String)
        Dim ids As HashSet(Of String) = Nothing

        If cache.TryGetValue(htmlPath, ids) Then
            Return ids
        End If

        ids = New HashSet(Of String)(StringComparer.Ordinal)

        For Each m As Match In idRegex.Matches(IO.File.ReadAllText(htmlPath))
            Call ids.Add(m.Groups("id").Value)
        Next

        cache(htmlPath) = ids

        Return ids
    End Function

    Private Function relative(root As String, htmlPath As String) As String
        If htmlPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) Then
            Return htmlPath.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar)
        End If

        Return htmlPath
    End Function
End Module
