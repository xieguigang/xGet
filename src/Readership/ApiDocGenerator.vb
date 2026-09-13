Imports System.IO
Imports System.Text
Imports Microsoft.VisualBasic.ApplicationServices.Development.XmlDoc.Assembly

''' <summary>
''' The build result of the <see cref="ApiDoc"/> api reference document generator.
''' </summary>
Public Class DocBuildResult

    ''' <summary>
    ''' the output directory of the generated document site
    ''' </summary>
    ''' <returns></returns>
    Public Property Output As String

    ''' <summary>
    ''' the name of the theme that is used by the generated document site
    ''' </summary>
    ''' <returns></returns>
    Public Property Theme As String

    ''' <summary>
    ''' the site title
    ''' </summary>
    ''' <returns></returns>
    Public Property Title As String

    ''' <summary>
    ''' the assembly names that the document site covers
    ''' </summary>
    ''' <returns></returns>
    Public Property Assemblies As String() = {}

    Public Property NamespaceCount As Integer
    Public Property TypeCount As Integer
    Public Property MemberCount As Integer
    Public Property PageCount As Integer

    ''' <summary>
    ''' the non fatal problems that are found during the site generation
    ''' </summary>
    ''' <returns></returns>
    Public ReadOnly Property Warnings As New List(Of String)

    Public Overrides Function ToString() As String
        Return $"pages={PageCount}; namespaces={NamespaceCount}; types={TypeCount}; members={MemberCount}"
    End Function
End Class

''' <summary>
''' The result of the data extract stage of the api reference document
''' generator: the extracted document data and the non fatal warnings.
''' </summary>
Public Class ApiDocExtractResult

    ''' <summary>
    ''' the extracted, serializable document data
    ''' </summary>
    ''' <returns></returns>
    Public Property Document As ApiDocDocument

    ''' <summary>
    ''' the non fatal problems that are found during the extraction
    ''' </summary>
    ''' <returns></returns>
    Public ReadOnly Property Warnings As New List(Of String)

    Public Overrides Function ToString() As String
        If Document Is Nothing Then
            Return "namespaces=0; types=0; members=0"
        End If

        Return $"namespaces={Document.NamespaceCount()}; types={Document.TypeCount()}; members={Document.MemberCount()}"
    End Function
End Class

''' <summary>
''' The api reference document generator: it loads the .net xml comment documents
''' (see <see cref="ProjectSpace"/>) and generates the api reference documents.
''' 
''' The generation is split into two independent steps:
''' 
''' + <see cref="Extract"/>: parse the xml comment documents (and optionally
'''   reflect the sibling clr assemblies to supplement the missing members) and
'''   produce a serializable <see cref="ApiDocDocument"/>;
''' + the page renderers (see <see cref="IndexPageWriter"/>,
'''   <see cref="TypePageWriter"/> and <see cref="ApiDocRenderer"/>): render a
'''   specific page from the extracted document data.
''' </summary>
Public Module ApiDoc

    ''' <summary>
    ''' Extract the document data from the given options. This is the first
    ''' (data extraction) step of the document generation.
    ''' </summary>
    ''' <param name="options"></param>
    ''' <returns></returns>
    Public Function Extract(options As ApiDocOptions) As ApiDocExtractResult
        If options Is Nothing Then
            Throw New ArgumentNullException(NameOf(options))
        End If

        Dim errorMessage$ = options.ValidateInput()

        If Not String.IsNullOrEmpty(errorMessage) Then
            Throw New ArgumentException(errorMessage, NameOf(options))
        End If

        Dim result As New ApiDocExtractResult
        Dim documents$() = resolveDocuments(options.Input, result.Warnings)

        If documents.Length = 0 Then
            Call result.Warnings.Add($"no xml comment document was found from: {options.Input}")
            result.Document = ApiDocDocument.Empty()
            Return result
        End If

        ' ---- load the xml comment documents ----
        Dim space As New ProjectSpace(excludeVBSpecific:=options.ExcludeVBSpecific)

        For Each document As String In documents
            Try
                Call space.ImportFromXmlDocFile(document)
            Catch ex As Exception
                Call result.Warnings.Add($"{document}: {ex.Message}")
            End Try
        Next

        result.Document = DocExtractor.BuildDocument(space, result.Warnings)

        With result.Document
            .title = options.Title
            .subTitle = options.SubTitle
            .description = options.Description
        End With

        ' ---- reflect the sibling clr assemblies to supplement the missing members ----
        If options.ReflectSupplement Then
            Try
                Call DocReflection.Supplement(result.Document, documents, result.Warnings)
            Catch ex As Exception
                Call result.Warnings.Add($"reflection supplement failed: {ex.Message}")
            End Try
        End If

        If result.Document.TypeCount() = 0 Then
            Call result.Warnings.Add("no type was loaded from the given xml comment documents.")
        End If

        ' the extracted document carries the urls of the offline static site by default
        Call DocUrls.ApplyStaticSite(result.Document)

        Return result
    End Function

    ''' <summary>
    ''' Extract the document data and generate the offline static html document
    ''' site from the given options.
    ''' </summary>
    ''' <param name="options"></param>
    ''' <returns></returns>
    Public Function Generate(options As ApiDocOptions) As DocBuildResult
        Dim extracted As ApiDocExtractResult = Extract(options)
        Dim document As ApiDocDocument = extracted.Document

        Dim result As New DocBuildResult With {
            .Output = Path.GetFullPath(options.Output),
            .Title = options.Title
        }

        Call result.Warnings.AddRange(extracted.Warnings)

        ' ---- output directory & theme assets ----
        Call Directory.CreateDirectory(result.Output)

        If options.Clean Then
            Call cleanOutput(result.Output)
        End If

        Dim theme As ThemeBundle = ThemeAssets.Resolve(options.Theme, result.Output)
        result.Theme = theme.Name

        ' ---- render the pages ----
        Dim ctx As New DocSiteContext With {
            .Document = document,
            .Index = New ApiDocIndex(document),
            .Options = options,
            .Theme = theme,
            .AbsoluteUrls = False
        }

        Call writeFile(result.Output, "index.html", IndexPageWriter.Render(ctx))
        result.PageCount += 1

        For Each ns As ApiDocNamespace In document.namespaces
            Call writeFile(result.Output, ns.url, NamespacePageWriter.Render(ctx, ns))
            result.PageCount += 1
        Next

        For Each t As ApiDocType In document.types
            Call writeFile(result.Output, t.url, TypePageWriter.Render(ctx, t))
            result.PageCount += 1
        Next

        ' ---- build result ----
        result.Assemblies = document.AssemblyNames()
        result.NamespaceCount = document.NamespaceCount()
        result.TypeCount = document.TypeCount()
        result.MemberCount = document.MemberCount()

        Return result
    End Function

    ''' <summary>
    ''' Generate the api reference document site with the minimal arguments.
    ''' </summary>
    ''' <param name="input"></param>
    ''' <param name="output"></param>
    ''' <param name="theme"></param>
    ''' <returns></returns>
    Public Function Generate(input As String, output As String, Optional theme$ = Nothing) As DocBuildResult
        Return Generate(New ApiDocOptions With {
            .Input = input,
            .Output = output,
            .Theme = If(String.IsNullOrWhiteSpace(theme), ThemeAssets.DefaultTheme, theme)
        })
    End Function

    Private Function resolveDocuments(input As String, warnings As List(Of String)) As String()
        If Directory.Exists(input) Then
            Return Directory _
                .GetFiles(input, "*.xml", SearchOption.TopDirectoryOnly) _
                .OrderBy(Function(f) f, StringComparer.OrdinalIgnoreCase) _
                .ToArray
        End If

        Dim ext$ = Path.GetExtension(input).ToLowerInvariant

        If ext = ".xml" Then
            Return {input}
        End If

        ' the input is a clr assembly, so the sibling xml comment document is used
        Dim xml$ = Path.ChangeExtension(input, ".xml")

        If File.Exists(xml) Then
            Return {xml}
        End If

        Call warnings.Add($"the xml comment document was not found next to the assembly: {xml}")

        Return {}
    End Function

    Private Sub cleanOutput(output As String)
        For Each name As String In {"index.html", "favicon.png", "favicon.ico", "namespaces", "types", "assets"}
            Dim target$ = Path.Combine(output, name)

            Try
                If Directory.Exists(target) Then
                    Call Directory.Delete(target, recursive:=True)
                ElseIf File.Exists(target) Then
                    Call File.Delete(target)
                End If
            Catch ex As Exception
                ' the locked file should not break the whole site generation
            End Try
        Next
    End Sub

    Private Sub writeFile(output As String, siteUrl As String, html As String)
        Dim target$ = Path.Combine(output, siteUrl.Replace("/"c, Path.DirectorySeparatorChar))
        Dim dir$ = Path.GetDirectoryName(target)

        If Not String.IsNullOrEmpty(dir) Then
            Call Directory.CreateDirectory(dir)
        End If

        Call File.WriteAllText(target, html, New UTF8Encoding(encoderShouldEmitUTF8Identifier:=False))
    End Sub
End Module
