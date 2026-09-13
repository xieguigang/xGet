Imports Microsoft.VisualBasic.ApplicationServices
Imports Microsoft.VisualBasic.Linq

''' <summary>
''' The in memory index of an <see cref="ApiDocDocument"/>. It provides the
''' namespace / type lookup, the type grouping of a namespace, the namespace
''' tree of the sidebar and the ``cref`` identity to page url resolution.
''' 
''' The index is a pure derived view of the document data: it is rebuilt on
''' demand and never serialized.
''' </summary>
Public Class ApiDocIndex

    Public ReadOnly Property Document As ApiDocDocument

    ''' <summary>
    ''' the ``cref`` identity to the site relative url index
    ''' </summary>
    ''' <returns></returns>
    Public ReadOnly Property Xref As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)

    ''' <summary>
    ''' the namespace entry index which is keyed by the full namespace name
    ''' </summary>
    ''' <returns></returns>
    Public ReadOnly Property NamespaceByName As New Dictionary(Of String, ApiDocNamespace)(StringComparer.OrdinalIgnoreCase)

    ''' <summary>
    ''' the total type count of every namespace, the types of the descendant
    ''' namespaces are included. it is keyed by the full namespace name.
    ''' </summary>
    ''' <returns></returns>
    Public ReadOnly Property NamespaceTypeCount As New Dictionary(Of String, Integer)(StringComparer.OrdinalIgnoreCase)

    ''' <summary>
    ''' the namespace tree of the whole document. every tree node is one
    ''' namespace segment and the <see cref="FileSystemTree.Name"/> is the
    ''' segment name.
    ''' </summary>
    ''' <returns></returns>
    Public Property NamespaceTree As FileSystemTree

    Private ReadOnly typesByNamespace As New Dictionary(Of String, List(Of ApiDocType))(StringComparer.OrdinalIgnoreCase)
    Private ReadOnly typeByFullName As New Dictionary(Of String, ApiDocType)(StringComparer.OrdinalIgnoreCase)

    Public Sub New(document As ApiDocDocument)
        Me.Document = If(document, ApiDocDocument.Empty())
        Call Me.Document.EnsureTables()

        For Each ns As ApiDocNamespace In Me.Document.namespaces
            Dim name$ = If(ns.name, "")

            If Not NamespaceByName.ContainsKey(name) Then
                NamespaceByName(name) = ns
            End If
        Next

        For Each t As ApiDocType In Me.Document.types
            Dim nsName$ = If(t.namespaceName, "")

            If Not typeByFullName.ContainsKey(If(t.fullName, "")) Then
                typeByFullName(If(t.fullName, "")) = t
            End If

            Dim list As List(Of ApiDocType) = Nothing

            If Not typesByNamespace.TryGetValue(nsName, list) Then
                list = New List(Of ApiDocType)
                typesByNamespace(nsName) = list
            End If

            Call list.Add(t)
        Next

        Call buildXref()
        Call countNamespaceTypes()

        Me.NamespaceTree = buildNamespaceTree(
            Me.Document.namespaces _
                .Where(Function(n) Not String.IsNullOrWhiteSpace(n.name)) _
                .Select(Function(n) n.name))
    End Sub

    ''' <summary>
    ''' the types that are declared in the given namespace
    ''' </summary>
    ''' <param name="namespaceName"></param>
    ''' <returns></returns>
    Public Function TypesOf(namespaceName As String) As IEnumerable(Of ApiDocType)
        Dim list As List(Of ApiDocType) = Nothing

        If typesByNamespace.TryGetValue(If(namespaceName, ""), list) Then
            Return list
        End If

        Return Enumerable.Empty(Of ApiDocType)()
    End Function

    ''' <summary>
    ''' find the namespace entry by its full name
    ''' </summary>
    ''' <param name="namespaceName"></param>
    ''' <returns></returns>
    Public Function FindNamespace(namespaceName As String) As ApiDocNamespace
        Dim entry As ApiDocNamespace = Nothing

        If NamespaceByName.TryGetValue(If(namespaceName, ""), entry) Then
            Return entry
        End If

        Return Nothing
    End Function

    ''' <summary>
    ''' find a type entry by its full name
    ''' </summary>
    ''' <param name="fullName"></param>
    ''' <returns></returns>
    Public Function FindType(fullName As String) As ApiDocType
        Dim entry As ApiDocType = Nothing

        If typeByFullName.TryGetValue(If(fullName, ""), entry) Then
            Return entry
        End If

        Return Nothing
    End Function

    ''' <summary>
    ''' the total type count of the given namespace, including the types of the
    ''' descendant namespaces.
    ''' </summary>
    ''' <param name="namespaceName"></param>
    ''' <returns></returns>
    Public Function TypeCountOf(namespaceName As String) As Integer
        Dim total As Integer = 0

        If NamespaceTypeCount.TryGetValue(If(namespaceName, ""), total) Then
            Return total
        End If

        Return 0
    End Function

    ''' <summary>
    ''' Resolve a ``cref`` identity into a site relative url. Nothing will be
    ''' returned when the target could not be found in the current document.
    ''' </summary>
    ''' <param name="cref"></param>
    ''' <returns></returns>
    Public Function ResolveUrl(cref As String) As String
        If String.IsNullOrWhiteSpace(cref) Then
            Return Nothing
        End If

        Dim id$ = cref.Trim()
        Dim url As String = Nothing

        If Xref.TryGetValue(id, url) Then
            Return url
        End If

        Dim kind As Char = " "c
        Dim body$ = id

        If id.Length > 2 AndAlso id(1) = ":"c Then
            kind = id(0)
            body = id.Substring(2)
        End If

        If Xref.TryGetValue(body, url) Then
            Return url
        End If

        Dim noParams$ = DocNaming.StripParams(body)

        If noParams <> body Then
            If Xref.TryGetValue(noParams, url) Then
                Return url
            End If

            If kind <> " "c AndAlso Xref.TryGetValue(kind & ":" & noParams, url) Then
                Return url
            End If

            ' fallback to the owner type of the referenced member
            Dim p = noParams.LastIndexOf("."c)

            If p > 0 Then
                Dim owner$ = noParams.Substring(0, p)

                If Xref.TryGetValue("T:" & owner, url) Then
                    Return url
                End If

                If Xref.TryGetValue(owner, url) Then
                    Return url
                End If
            End If
        Else
            Dim p = body.LastIndexOf("."c)

            If p > 0 AndAlso Xref.TryGetValue("T:" & body.Substring(0, p), url) Then
                Return url
            End If
        End If

        ' the last resort: the short name only
        Dim tokens = noParams.Split("."c)
        Dim shortName$ = tokens(tokens.Length - 1)

        If Xref.TryGetValue(shortName, url) Then
            Return url
        End If

        Return Nothing
    End Function

    Private Sub buildXref()
        For Each ns As ApiDocNamespace In Document.namespaces
            Call addXref("N:" & ns.name, ns.url)
            Call addXref(ns.name, ns.url)
        Next

        For Each t As ApiDocType In Document.types
            Call addXref("T:" & t.fullName, t.url)
            Call addXref(t.fullName, t.url)
            Call addXref(t.name, t.url)

            If t.members Is Nothing Then
                Continue For
            End If

            For Each m As ApiDocMember In t.members
                Dim target$ = t.url & "#" & m.anchor
                Dim decl$ = If(m.declaration, m.name)
                Dim noParams$ = DocNaming.StripParams(decl)

                Call addXref(m.kindChar & ":" & decl, target)
                Call addXref(decl, target)

                If noParams <> decl Then
                    Call addXref(m.kindChar & ":" & noParams, target)
                    Call addXref(noParams, target)
                End If
            Next
        Next
    End Sub

    Private Sub addXref(key As String, url As String)
        If String.IsNullOrWhiteSpace(key) OrElse String.IsNullOrWhiteSpace(url) Then
            Return
        End If

        If Not Xref.ContainsKey(key) Then
            Xref(key) = url
        End If
    End Sub

    ''' <summary>
    ''' accumulate the type count of every namespace, every ancestor namespace
    ''' accumulates the type count of all of its descendant namespaces.
    ''' </summary>
    Private Sub countNamespaceTypes()
        Dim globalCount As Integer = 0

        For Each ns As ApiDocNamespace In Document.namespaces
            Dim count As Integer = TypesOf(ns.name).Count()

            If String.IsNullOrWhiteSpace(ns.name) Then
                globalCount += count
                Continue For
            End If

            Dim path$ = ""

            For Each segment As String In ns.name.Split("."c)
                If segment.Length = 0 Then
                    Continue For
                End If

                path = If(path.Length = 0, segment, path & "." & segment)

                Dim total As Integer = 0

                If NamespaceTypeCount.TryGetValue(path, total) Then
                    NamespaceTypeCount(path) = total + count
                Else
                    NamespaceTypeCount(path) = count
                End If
            Next
        Next

        NamespaceTypeCount("") = globalCount
    End Sub

    ''' <summary>
    ''' build the namespace tree from the dot separated namespace names. every
    ''' tree node is one namespace segment.
    ''' </summary>
    ''' <param name="names"></param>
    ''' <returns></returns>
    Private Shared Function buildNamespaceTree(names As IEnumerable(Of String)) As FileSystemTree
        Dim root As New FileSystemTree With {
            .Name = Nothing,
            .Parent = Nothing,
            .Files = New Dictionary(Of String, FileSystemTree)
        }

        For Each namespaceName As String In names
            Dim node As FileSystemTree = root
            Dim segments = namespaceName.Replace("."c, "/"c).Split("/"c)

            For Each segment As String In segments
                If segment.Length = 0 Then
                    Continue For
                End If

                If node.Files.ContainsKey(segment) Then
                    node = node.Files(segment)
                Else
                    node = node.AddFile(segment)
                End If
            Next

            node.data = namespaceName
        Next

        Return root
    End Function
End Class
