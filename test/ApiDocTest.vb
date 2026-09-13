Imports System.IO
Imports System.Text.Json
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
        Dim extractOk As Boolean = verifyExtract(input)

        Call Console.WriteLine()
        Call Console.WriteLine($"validation: {If(ok AndAlso extractOk, "PASS", "FAIL")}")

        Return If(ok AndAlso extractOk, 0, 1)
    End Function

    ''' <summary>
    ''' verify the data extract stage: the extracted document model must be
    ''' equivalent to the generated site and it must survive a json round trip,
    ''' because the nuget server stores the document data as json.
    ''' </summary>
    ''' <param name="input"></param>
    ''' <returns></returns>
    Private Function verifyExtract(input As String) As Boolean
        Call Console.WriteLine()
        Call Console.WriteLine("extract stage:")

        Dim extract As ApiDocExtractResult

        Try
            extract = ApiDoc.Extract(New ApiDocOptions With {
                .Input = input,
                .Clean = False
            })
        Catch ex As Exception
            Call Console.WriteLine($"  extract failed: {ex.Message}")
            Return False
        End Try

        Dim document As ApiDocDocument = extract.Document
        Dim supplemented As Integer = 0

        For Each t As ApiDocType In document.types
            For Each m As ApiDocMember In t.members
                If String.IsNullOrWhiteSpace(m.summary) AndAlso Not String.IsNullOrWhiteSpace(m.declaration) Then
                    supplemented += 1
                End If
            Next
        Next

        For Each w As String In extract.Warnings
            Call Console.WriteLine($"  warning: {w}")
        Next

        Dim json As String = JsonSerializer.Serialize(document)
        Dim roundTrip As ApiDocDocument = JsonSerializer.Deserialize(Of ApiDocDocument)(json)

        Call Console.WriteLine($"  {extract}")
        Call Console.WriteLine($"  json payload : {json.Length} chars, round trip {roundTrip}")
        Call Console.WriteLine($"  no comment members (reflection supplemented): {supplemented}")

        Dim ok As Boolean = roundTrip.TypeCount() = document.TypeCount() AndAlso
            roundTrip.MemberCount() = document.MemberCount()

        If Not ok Then
            Call Console.WriteLine("  the document json round trip lost data.")
        End If

        Return ok
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
        Dim paramTypeCells As Integer = 0

        Call Console.WriteLine($"  html pages: {htmlPages.Length}")
        Call reportDistribution(root, htmlPages)

        For Each htmlFile As String In htmlPages
            Dim html$ = IO.File.ReadAllText(htmlFile)
            Dim pageDir$ = Path.GetDirectoryName(htmlFile)

            If html.Contains("href=""cref:") Then
                Call problems.Add($"{relative(root, htmlFile)}: the cref url was not resolved")
            End If

            crefResolved += Regex.Matches(html, "<a class=""cref""").Count
            crefExternal += Regex.Matches(html, "<code class=""cref""").Count
            paramTypeCells += Regex.Matches(html, "<td class=""ptype"">").Count

            ' the NamespaceDoc magic type only carries the document of its namespace,
            ' it must not be generated as an independent type page.
            If String.Equals(Path.GetFileNameWithoutExtension(htmlFile), "NamespaceDoc", StringComparison.Ordinal) Then
                Call problems.Add($"{relative(root, htmlFile)}: the NamespaceDoc magic type is generated as an independent page")
            End If

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
        Call Console.WriteLine($"  parameters: {paramTypeCells} parameter type links")
        Call Console.WriteLine($"  problems  : {problems.Count}")

        For i As Integer = 0 To Math.Min(problems.Count, 30) - 1
            Call Console.WriteLine($"    - {problems(i)}")
        Next

        Return problems.Count = 0
    End Function

    ''' <summary>
    ''' report how the generated pages are distributed in the folders. the pages
    ''' are grouped into the namespace folders, so the max file count of a single
    ''' folder should stay small even for a very large project.
    ''' </summary>
    ''' <param name="root"></param>
    ''' <param name="htmlPages"></param>
    Private Sub reportDistribution(root As String, htmlPages As String())
        Dim perFolder As New Dictionary(Of String, Integer)(StringComparer.OrdinalIgnoreCase)
        Dim longest As Integer = 0
        Dim deepest As Integer = 0

        For Each htmlFile As String In htmlPages
            Dim folder$ = Path.GetDirectoryName(htmlFile)
            Dim count As Integer = 0

            If perFolder.TryGetValue(folder, count) Then
                perFolder(folder) = count + 1
            Else
                perFolder(folder) = 1
            End If

            Dim rel$ = relative(root, htmlFile)

            If rel.Length > longest Then
                longest = rel.Length
            End If

            Dim depth As Integer = rel.Split(Path.DirectorySeparatorChar).Length - 1

            If depth > deepest Then
                deepest = depth
            End If
        Next

        Dim maxFiles As Integer = 0
        Dim maxFolder$ = ""

        For Each item As KeyValuePair(Of String, Integer) In perFolder
            If item.Value > maxFiles Then
                maxFiles = item.Value
                maxFolder = relative(root, item.Key)
            End If
        Next

        Call Console.WriteLine($"  folders   : {perFolder.Count} folders, max {maxFiles} html in '{(If(String.IsNullOrEmpty(maxFolder), ".", maxFolder))}'")
        Call Console.WriteLine($"  path      : max folder depth {deepest}, longest relative path {longest} chars")
    End Sub

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
