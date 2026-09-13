Imports System.Text
Imports System.Text.Json.Serialization

''' <summary>
''' A parameter document or a generic type parameter document.
''' </summary>
Public Class ApiDocParam

    ''' <summary>
    ''' the parameter name
    ''' </summary>
    ''' <returns></returns>
    Public Property name As String

    ''' <summary>
    ''' the markdown comment text of the parameter
    ''' </summary>
    ''' <returns></returns>
    Public Property text As String

    Public Overrides Function ToString() As String
        Return name
    End Function
End Class

''' <summary>
''' A method / property / field / event document of one type. This is a plain
''' serializable data object: it carries all of the comment texts that are
''' needed by the page renderer, and it is completely decoupled from the live
''' xml comment document model of the base code library.
''' </summary>
Public Class ApiDocMember

    ''' <summary>
    ''' the member kind: ``method`` / ``property`` / ``field`` / ``event``
    ''' </summary>
    ''' <returns></returns>
    Public Property kind As String

    ''' <summary>
    ''' the doc id kind character: ``M`` / ``P`` / ``F`` / ``E``
    ''' </summary>
    ''' <returns></returns>
    Public Property kindChar As String

    ''' <summary>
    ''' the short member name
    ''' </summary>
    ''' <returns></returns>
    Public Property name As String

    ''' <summary>
    ''' the full declare information, example as ``Ns.Type.Method(System.String)``
    ''' </summary>
    ''' <returns></returns>
    Public Property declaration As String

    ''' <summary>
    ''' the html anchor of this member inside its type page
    ''' </summary>
    ''' <returns></returns>
    Public Property anchor As String

    ''' <summary>
    ''' the zero based overload index of this member
    ''' </summary>
    ''' <returns></returns>
    Public Property overloadIndex As Integer

    Public Property summary As String
    Public Property remarks As String
    Public Property returns As String
    Public Property example As String

    Public Property parameters As List(Of ApiDocParam)
    Public Property typeParams As List(Of ApiDocParam)

    ''' <summary>
    ''' the human readable kind name, example as ``method``
    ''' </summary>
    ''' <returns></returns>
    <JsonIgnore>
    Public ReadOnly Property kindName As String
        Get
            Select Case If(kindChar, "")
                Case "M"c
                    Return "method"
                Case "P"c
                    Return "property"
                Case "F"c
                    Return "field"
                Case "E"c
                    Return "event"
                Case Else
                    Return "member"
            End Select
        End Get
    End Property

    ''' <summary>
    ''' the display declare text without the ``Ns.Type.`` prefix
    ''' </summary>
    ''' <param name="fullTypeName"></param>
    ''' <returns></returns>
    Public Function signature(fullTypeName As String) As String
        Dim decl$ = If(declaration, name)

        If Not String.IsNullOrEmpty(fullTypeName) Then
            If decl.StartsWith(fullTypeName & ".", StringComparison.Ordinal) Then
                Return decl.Substring(fullTypeName.Length + 1)
            End If
        End If

        Return decl
    End Function

    Public Overrides Function ToString() As String
        Return If(declaration, name)
    End Function
End Class

''' <summary>
''' A type document of the api reference document. The type page is rendered
''' from this data object, and the object itself is the per type payload that
''' the nuget server stores in its api document table.
''' </summary>
Public Class ApiDocType

    ''' <summary>
    ''' the clr type short name, it may contains the generic arity suffix
    ''' </summary>
    ''' <returns></returns>
    Public Property name As String

    ''' <summary>
    ''' the full name of this type, example as ``Ns.ClassName``
    ''' </summary>
    ''' <returns></returns>
    Public Property fullName As String

    ''' <summary>
    ''' the name of the assembly that this type is defined in
    ''' </summary>
    ''' <returns></returns>
    Public Property project As String

    ''' <summary>
    ''' the full name of the namespace that contains this type
    ''' </summary>
    ''' <returns></returns>
    Public Property namespaceName As String

    ''' <summary>
    ''' the (context dependent) url of this type page
    ''' </summary>
    ''' <returns></returns>
    Public Property url As String

    Public Property summary As String
    Public Property remarks As String
    Public Property typeParams As List(Of ApiDocParam)

    Public Property members As List(Of ApiDocMember)

    ''' <summary>
    ''' the member count, it is used as the fallback when the member data itself
    ''' is not loaded (for example the document index pages of the nuget server)
    ''' </summary>
    ''' <returns></returns>
    Public Property memberCountHint As Integer

    ''' <summary>
    ''' the nuget package id that this type belongs to. It is only used by the
    ''' nuget server side document pages and it is empty for the offline static
    ''' document site.
    ''' </summary>
    ''' <returns></returns>
    Public Property packageId As String

    ''' <summary>
    ''' the nuget package version that this type document was extracted from
    ''' </summary>
    ''' <returns></returns>
    Public Property packageVersion As String

    Public Function GetMemberGroups(kind As Char) As IEnumerable(Of IGrouping(Of String, ApiDocMember))
        If members Is Nothing Then
            Return Enumerable.Empty(Of IGrouping(Of String, ApiDocMember))()
        End If

        Return members _
            .Where(Function(m) m.kindChar = kind) _
            .GroupBy(Function(m) m.name)
    End Function

    Public Function GetMembers(kind As Char) As IEnumerable(Of ApiDocMember)
        If members Is Nothing Then
            Return Enumerable.Empty(Of ApiDocMember)()
        End If

        Return members.Where(Function(m) m.kindChar = kind)
    End Function

    Public Function MemberCount() As Integer
        If members IsNot Nothing AndAlso members.Count > 0 Then
            Return members.Count
        End If

        Return memberCountHint
    End Function

    Public Overrides Function ToString() As String
        Return fullName
    End Function
End Class

''' <summary>
''' A namespace document of the api reference document site.
''' </summary>
Public Class ApiDocNamespace

    ''' <summary>
    ''' the full name of this namespace
    ''' </summary>
    ''' <returns></returns>
    Public Property name As String

    ''' <summary>
    ''' the name of the assembly that this namespace is defined in
    ''' </summary>
    ''' <returns></returns>
    Public Property project As String

    ''' <summary>
    ''' the (context dependent) url of this namespace page
    ''' </summary>
    ''' <returns></returns>
    Public Property url As String

    Public Property summary As String
    Public Property remarks As String

    Public Overrides Function ToString() As String
        Return name
    End Function
End Class

''' <summary>
''' The serializable api reference document model. It is produced by the
''' extract stage (see <see cref="ApiDoc.Extract"/>) and it is consumed by the
''' page renderers, so that the data extraction and the page rendering are two
''' independent steps.
''' </summary>
Public Class ApiDocDocument

    Public Property title As String
    Public Property subTitle As String
    Public Property description As String

    ''' <summary>
    ''' the assembly names that this document covers
    ''' </summary>
    ''' <returns></returns>
    Public Property assemblies As List(Of String)

    Public Property namespaces As List(Of ApiDocNamespace)
    Public Property types As List(Of ApiDocType)

    ''' <summary>
    ''' the nuget package id, it is empty for the offline static document site
    ''' </summary>
    ''' <returns></returns>
    Public Property packageId As String

    ''' <summary>
    ''' the nuget package version, it is empty for the offline static document site
    ''' </summary>
    ''' <returns></returns>
    Public Property packageVersion As String

    Public Function NamespaceCount() As Integer
        If namespaces Is Nothing Then
            Return 0
        End If
        Return namespaces.Count
    End Function

    Public Function TypeCount() As Integer
        If types Is Nothing Then
            Return 0
        End If
        Return types.Count
    End Function

    Public Function MemberCount() As Integer
        If types Is Nothing Then
            Return 0
        End If
        Return types.Sum(Function(t) t.MemberCount())
    End Function

    Public Function AssemblyNames() As String()
        If assemblies Is Nothing Then
            Return {}
        End If
        Return assemblies.ToArray
    End Function

    ''' <summary>
    ''' create an empty document which is ready to be filled by the extractor
    ''' </summary>
    ''' <returns></returns>
    Public Shared Function Empty() As ApiDocDocument
        Return New ApiDocDocument With {
            .assemblies = New List(Of String),
            .namespaces = New List(Of ApiDocNamespace),
            .types = New List(Of ApiDocType)
        }
    End Function

    ''' <summary>
    ''' ensure all of the nullable collections are initialized, so that a
    ''' document which is deserialized from the database is safe to use.
    ''' </summary>
    ''' <returns></returns>
    Public Function EnsureTables() As ApiDocDocument
        If assemblies Is Nothing Then assemblies = New List(Of String)
        If namespaces Is Nothing Then namespaces = New List(Of ApiDocNamespace)
        If types Is Nothing Then types = New List(Of ApiDocType)

        For Each t As ApiDocType In types
            If t.members Is Nothing Then t.members = New List(Of ApiDocMember)
            If t.typeParams Is Nothing Then t.typeParams = New List(Of ApiDocParam)

            For Each m As ApiDocMember In t.members
                If m.parameters Is Nothing Then m.parameters = New List(Of ApiDocParam)
                If m.typeParams Is Nothing Then m.typeParams = New List(Of ApiDocParam)
            Next
        Next

        Return Me
    End Function
End Class
