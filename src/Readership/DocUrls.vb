''' <summary>
''' The url scheme helpers of the api reference document. The document data
''' itself is url scheme agnostic: the offline static document site and the
''' nuget server side document pages assign different page urls to the very
''' same document data.
''' </summary>
Public Module DocUrls

    ''' <summary>
    ''' the site relative url of a namespace page, example as
    ''' ``namespaces/Microsoft/VisualBasic/My.html``
    ''' </summary>
    ''' <param name="namespaceName"></param>
    ''' <param name="used"></param>
    ''' <returns></returns>
    Public Function NamespaceUrl(namespaceName As String, used As HashSet(Of String)) As String
        Dim folder$ = DocNaming.NamespaceFolder(namespaceName)
        Dim unique$ = folder
        Dim n As Integer = 1

        While used.Contains(unique)
            n += 1
            unique = folder & "-" & n.ToString
        End While

        Call used.Add(unique)

        Return "namespaces/" & unique & ".html"
    End Function

    ''' <summary>
    ''' the site relative url of a type page, example as
    ''' ``types/Microsoft/VisualBasic/My/App.html``. the type pages are grouped
    ''' into the folder of their namespace.
    ''' </summary>
    ''' <param name="namespaceName"></param>
    ''' <param name="typeName"></param>
    ''' <param name="used">the used file names of every type folder</param>
    ''' <returns></returns>
    Public Function TypeUrl(namespaceName As String, typeName As String, used As Dictionary(Of String, HashSet(Of String))) As String
        Dim folder$ = "types/" & DocNaming.NamespaceFolder(namespaceName)
        Dim perFolder As HashSet(Of String) = Nothing

        If Not used.TryGetValue(folder, perFolder) Then
            perFolder = New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            used(folder) = perFolder
        End If

        Dim baseName$ = DocNaming.Slug(typeName)
        Dim unique$ = baseName
        Dim n As Integer = 1

        While perFolder.Contains(unique)
            n += 1
            unique = baseName & "-" & n.ToString
        End While

        Call perFolder.Add(unique)

        Return folder & "/" & unique & ".html"
    End Function

    ''' <summary>
    ''' assign the naviagtion urls of the offline static document site to the
    ''' given document.
    ''' </summary>
    ''' <param name="document"></param>
    Public Sub ApplyStaticSite(document As ApiDocDocument)
        If document Is Nothing Then
            Return
        End If

        Call document.EnsureTables()

        Dim nsUrls As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        Dim typeSlugs As New Dictionary(Of String, HashSet(Of String))(StringComparer.OrdinalIgnoreCase)

        For Each ns As ApiDocNamespace In document.namespaces
            ns.url = NamespaceUrl(ns.name, nsUrls)
        Next

        For Each t As ApiDocType In document.types
            t.url = TypeUrl(t.namespaceName, t.name, typeSlugs)
        Next
    End Sub

    ''' <summary>
    ''' the url of a type page which is served by the nuget api document pages.
    ''' </summary>
    ''' <param name="baseUrl">an optional url prefix, example as ``/``</param>
    ''' <param name="packageId"></param>
    ''' <param name="version"></param>
    ''' <param name="typeFullName"></param>
    ''' <returns></returns>
    Public Function NugetTypeUrl(baseUrl As String, packageId As String, version As String, typeFullName As String) As String
        Return If(baseUrl, "").TrimEnd("/"c) &
            "/docs/" & Uri.EscapeDataString(If(packageId, "")) &
            "/" & Uri.EscapeDataString(If(version, "")) &
            "/" & Uri.EscapeDataString(If(typeFullName, "")) & ".html"
    End Function

    ''' <summary>
    ''' assign the urls of the nuget api document pages to the given document.
    ''' every type carries its own package id / version, so that a merged global
    ''' index document can link the types of different packages.
    ''' </summary>
    ''' <param name="document"></param>
    ''' <param name="baseUrl"></param>
    Public Sub ApplyNugetScheme(document As ApiDocDocument, Optional baseUrl As String = "")
        If document Is Nothing Then
            Return
        End If

        Call document.EnsureTables()

        For Each t As ApiDocType In document.types
            Dim packageId$ = If(t.packageId, document.packageId)
            Dim version$ = If(t.packageVersion, document.packageVersion)
            t.url = NugetTypeUrl(baseUrl, packageId, version, t.fullName)
        Next

        ' the nuget document pages only have the global index, the package index
        ' and the type page, so a namespace is rendered as a group label instead
        ' of a hyper link.
        For Each ns As ApiDocNamespace In document.namespaces
            ns.url = ""
        Next
    End Sub
End Module
