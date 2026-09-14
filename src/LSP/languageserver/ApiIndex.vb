Imports System.Collections.Concurrent
Imports System.Collections.Generic
Imports System.Linq
Imports System.Text.Json
Imports Nuget
Imports Readership

''' <summary>
''' one row of the global, read only type index. it is built once at startup from
''' <see cref="NugetStore.ReadApiDocIndex"/> and never mutated afterwards, so it
''' is safe to share across every client session.
''' </summary>
Public Class TypeEntry

    ''' <summary>the package that owns this type (used to lazily load members).</summary>
    Public Property packageId As String

    ''' <summary>the package version that owns this type.</summary>
    Public Property version As String

    ''' <summary>the namespace the type lives in.</summary>
    Public Property namespaceName As String

    ''' <summary>the full name (namespace.type) of the type; the index lookup key.</summary>
    Public Property type_fullname As String

    ''' <summary>the markdown summary of the type (without its members).</summary>
    Public Property summary As String

    ''' <summary>the scalar member count hint of the type.</summary>
    Public Property memberCountHint As Integer
End Class

''' <summary>
''' the in memory view of the whole nuget api document database. the lightweight
''' scalar index is loaded at startup; the heavy per type member payloads are
''' loaded on demand, grouped by package version and cached, so that the first
''' completion/hover against a package is a little slower but repeated requests
''' are served from memory.
''' </summary>
Public Class ApiIndex

    Private ReadOnly store As NugetStore

    Private ReadOnly namespaces As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
    Private ReadOnly typesByFullName As New Dictionary(Of String, TypeEntry)(StringComparer.OrdinalIgnoreCase)

    ''' <summary>cache of the fully deserialized document of one package version.</summary>
    Private ReadOnly packageCache As New ConcurrentDictionary(Of String, Dictionary(Of String, ApiDocType))(StringComparer.OrdinalIgnoreCase)

    Private Shared ReadOnly JsonOptions As New JsonSerializerOptions With {
        .PropertyNameCaseInsensitive = True
    }

    Public Sub New(store As NugetStore)
        Me.store = store
    End Sub

    ''' <summary>number of distinct types indexed.</summary>
    Public ReadOnly Property TypeCount As Integer
        Get
            Return typesByFullName.Count
        End Get
    End Property

    ''' <summary>number of distinct namespaces indexed.</summary>
    Public ReadOnly Property NamespaceCount As Integer
        Get
            Return namespaces.Count
        End Get
    End Property

    ''' <summary>
    ''' build the scalar index from the database. when the database is empty this
    ''' simply produces an empty index, which keeps the server running but with no
    ''' completion items.
    ''' </summary>
    Public Sub Build()
        Dim rows As List(Of PackageApiDocRecord) = store.ReadApiDocIndex()

        ' when the same type exists in several package versions, keep only the
        ' newest one so that completion is not polluted by stale duplicates.
        Dim best As New Dictionary(Of String, TypeEntry)(StringComparer.OrdinalIgnoreCase)

        If rows IsNot Nothing Then
            For Each r As PackageApiDocRecord In rows
                If String.IsNullOrEmpty(r.type_fullname) Then
                    Continue For
                End If

                Dim entry As New TypeEntry With {
                    .packageId = r.package_id,
                    .version = r.version,
                    .namespaceName = r.namespace_name,
                    .type_fullname = r.type_fullname,
                    .summary = If(r.summary, ""),
                    .memberCountHint = r.member_count
                }

                Dim existing As TypeEntry = Nothing

                If best.TryGetValue(r.type_fullname, existing) Then
                    If NugetStore.VersionKey(r.version) > NugetStore.VersionKey(existing.version) Then
                        best(r.type_fullname) = entry
                    End If
                Else
                    best(r.type_fullname) = entry
                End If

                If Not String.IsNullOrEmpty(r.namespace_name) Then
                    Call namespaces.Add(r.namespace_name)
                End If
            Next
        End If

        For Each kvp As KeyValuePair(Of String, TypeEntry) In best
            typesByFullName(kvp.Key) = kvp.Value
        Next
    End Sub

    ''' <summary>look up a type entry by its full name (case insensitive).</summary>
    Public Function FindType(fullName As String) As TypeEntry
        Dim entry As TypeEntry = Nothing

        If typesByFullName.TryGetValue(fullName, entry) Then
            Return entry
        End If

        Return Nothing
    End Function

    ''' <summary>
    ''' find a type entry by its short name. this is used by the hover provider
    ''' when a bare identifier is inspected. returns <c>Nothing</c> when the name
    ''' is ambiguous or unknown.
    ''' </summary>
    Public Function FindTypeByName(name As String) As TypeEntry
        If String.IsNullOrEmpty(name) Then
            Return Nothing
        End If

        Dim found As TypeEntry = Nothing

        For Each entry As TypeEntry In typesByFullName.Values
            Dim shortName As String = entry.type_fullname

            If Not String.IsNullOrEmpty(entry.namespaceName) Then
                shortName = entry.type_fullname.Substring(entry.namespaceName.Length + 1)
            End If

            If String.Equals(shortName, name, StringComparison.OrdinalIgnoreCase) Then
                If found Is Nothing Then
                    found = entry
                Else
                    ' ambiguous: more than one type shares this short name
                    Return Nothing
                End If
            End If
        Next

        Return found
    End Function

    ''' <summary>all namespaces whose name starts with the given prefix.</summary>
    Public Function MatchNamespaces(prefix As String) As IEnumerable(Of String)
        If String.IsNullOrEmpty(prefix) Then
            Return namespaces
        End If

        Dim lower As String = prefix.ToLowerInvariant()
        Return namespaces.Where(Function(n) n.ToLowerInvariant().StartsWith(lower))
    End Function

    ''' <summary>
    ''' all distinct nested namespace names that live directly under
    ''' <paramref name="container"/> and start with <paramref name="fragment"/>.
    ''' </summary>
    Public Function MatchNestedNamespaces(container As String, fragment As String) As IEnumerable(Of String)
        Dim prefix As String = container & "."

        If String.IsNullOrEmpty(fragment) Then
            Return namespaces _
                .Where(Function(n) n.Length > prefix.Length AndAlso n.ToLowerInvariant().StartsWith(prefix.ToLowerInvariant()))
        End If

        Dim full As String = (prefix & fragment).ToLowerInvariant()
        Return namespaces _
            .Where(Function(n) n.Length > prefix.Length AndAlso n.ToLowerInvariant().StartsWith(full))
    End Function

    ''' <summary>
    ''' match types by their short name or their full name prefix. this powers the
    ''' top level (no dot) completion scenario.
    ''' </summary>
    Public Function MatchTypes(prefix As String) As IEnumerable(Of TypeEntry)
        If String.IsNullOrEmpty(prefix) Then
            Return typesByFullName.Values
        End If

        Dim lower As String = prefix.ToLowerInvariant()

        Return typesByFullName.Values.Where(
            Function(t)
                If t.type_fullname.ToLowerInvariant().StartsWith(lower) Then
                    Return True
                End If

                Dim shortName As String = t.type_fullname
                If Not String.IsNullOrEmpty(t.namespaceName) AndAlso t.type_fullname.Length > t.namespaceName.Length Then
                    shortName = t.type_fullname.Substring(t.namespaceName.Length + 1)
                End If

                Return shortName.ToLowerInvariant().StartsWith(lower)
            End Function)
    End Function

    ''' <summary>
    ''' match types that live in the given namespace and whose short name starts
    ''' with <paramref name="fragment"/>.
    ''' </summary>
    Public Function MatchTypesInNamespace(namespaceName As String, fragment As String) As IEnumerable(Of TypeEntry)
        Dim lower As String = If(fragment, "").ToLowerInvariant()

        Return typesByFullName.Values.Where(
            Function(t)
                If Not String.Equals(t.namespaceName, namespaceName, StringComparison.OrdinalIgnoreCase) Then
                    Return False
                End If

                If String.IsNullOrEmpty(lower) Then
                    Return True
                End If

                Dim shortName As String = t.type_fullname
                If t.type_fullname.Length > t.namespaceName.Length Then
                    shortName = t.type_fullname.Substring(t.namespaceName.Length + 1)
                End If

                Return shortName.ToLowerInvariant().StartsWith(lower)
            End Function)
    End Function

    ''' <summary>
    ''' return the deserialized members of a type. members are loaded lazily from
    ''' the owning package version and cached, so repeated calls are cheap.
    ''' </summary>
    Public Function GetMembers(fullName As String) As List(Of ApiDocMember)
        Dim type As ApiDocType = GetApiDocType(fullName)

        If type Is Nothing OrElse type.members Is Nothing Then
            Return New List(Of ApiDocMember)()
        End If

        Return type.members
    End Function

    ''' <summary>
    ''' return the fully deserialized type document (with members). used by the
    ''' hover provider to show the full signature of a type.
    ''' </summary>
    Public Function GetApiDocType(fullName As String) As ApiDocType
        Dim entry As TypeEntry = FindType(fullName)

        If entry Is Nothing Then
            Return Nothing
        End If

        Dim package As Dictionary(Of String, ApiDocType) = LoadPackage(entry.packageId, entry.version)
        Dim found As ApiDocType = Nothing

        If package.TryGetValue(fullName, found) Then
            Return found
        End If

        Return Nothing
    End Function

    ''' <summary>
    ''' load (and cache) the fully deserialized type documents of one package
    ''' version. the cache key is the package id combined with the version so that
    ''' different versions of the same package are kept apart.
    ''' </summary>
    Private Function LoadPackage(packageId As String, version As String) As Dictionary(Of String, ApiDocType)
        Dim cacheKey As String = packageId.ToLowerInvariant() & "@" & version.ToLowerInvariant()
        Dim cached As Dictionary(Of String, ApiDocType) = Nothing

        If packageCache.TryGetValue(cacheKey, cached) Then
            Return cached
        End If

        cached = New Dictionary(Of String, ApiDocType)(StringComparer.OrdinalIgnoreCase)

        Dim records As List(Of PackageApiDocRecord) = store.ReadPackageApiDocs(packageId, version)

        If records IsNot Nothing Then
            For Each r As PackageApiDocRecord In records
                If String.IsNullOrEmpty(r.payload) Then
                    Continue For
                End If

                Try
                    Dim t As ApiDocType = JsonSerializer.Deserialize(Of ApiDocType)(r.payload, JsonOptions)

                    If t IsNot Nothing AndAlso Not String.IsNullOrEmpty(t.fullName) Then
                        cached(t.fullName) = t
                    End If
                Catch
                    ' a single malformed payload must not break the whole package
                End Try
            Next
        End If

        Call packageCache.TryAdd(cacheKey, cached)
        Return cached
    End Function
End Class
