Imports System.IO
Imports System.Reflection

''' <summary>
''' The reflection based supplement of the api reference document. The xml
''' comment document that the compiler generates only contains the members that
''' actually carry a comment: for example the members of an enum which have no
''' xml comment are simply absent. This module reflects the sibling clr assembly
''' of a comment document and appends the missing members (public only) to the
''' document model, with an empty comment text.
''' </summary>
Public Module DocReflection

    Private ReadOnly sync As New Object
    Private ReadOnly loaded As New Dictionary(Of String, Assembly)(StringComparer.OrdinalIgnoreCase)

    ''' <summary>
    ''' supplement the document with the missing public members of every sibling
    ''' assembly of the given xml comment documents.
    ''' </summary>
    ''' <param name="document"></param>
    ''' <param name="xmlDocuments"></param>
    ''' <param name="warnings"></param>
    Public Sub Supplement(document As ApiDocDocument, xmlDocuments As IEnumerable(Of String), warnings As List(Of String))
        If document Is Nothing OrElse xmlDocuments Is Nothing Then
            Return
        End If

        For Each xml As String In xmlDocuments
            Dim assemblyPath As String = Path.ChangeExtension(xml, ".dll")

            If Not File.Exists(assemblyPath) Then
                Continue For
            End If

            Call SupplementAssembly(document, assemblyPath, warnings)
        Next
    End Sub

    ''' <summary>
    ''' supplement the document with the missing public members of one clr
    ''' assembly.
    ''' </summary>
    ''' <param name="document"></param>
    ''' <param name="assemblyPath"></param>
    ''' <param name="warnings"></param>
    Public Sub SupplementAssembly(document As ApiDocDocument, assemblyPath As String, warnings As List(Of String))
        Dim asm As Assembly = loadAssembly(assemblyPath, warnings)

        If asm Is Nothing Then
            Return
        End If

        Dim types As Dictionary(Of String, Type) = publicTypes(asm, assemblyPath, warnings)

        If types.Count = 0 Then
            Return
        End If

        For Each entry As ApiDocType In document.types
            Dim reflected As Type = Nothing

            If Not types.TryGetValue(If(entry.fullName, ""), reflected) Then
                Continue For
            End If

            Try
                Call supplementType(entry, reflected)
                Call DocExtractor.AssignMemberLayout(entry)
            Catch ex As Exception
                Call appendWarning(warnings, $"{assemblyPath}: {entry.fullName}: {ex.Message}")
            End Try
        Next
    End Sub

    Private Function loadAssembly(assemblyPath As String, warnings As List(Of String)) As Assembly
        SyncLock sync
            Dim cached As Assembly = Nothing

            If loaded.TryGetValue(assemblyPath, cached) Then
                Return cached
            End If

            Try
                Dim asm As Assembly = Assembly.LoadFrom(assemblyPath)
                loaded(assemblyPath) = asm
                Return asm
            Catch ex As Exception
                Call appendWarning(warnings, $"failed to load the assembly '{assemblyPath}': {ex.Message}")
                Return Nothing
            End Try
        End SyncLock
    End Function

    ''' <summary>
    ''' build the public type index of an assembly. A partially loadable assembly
    ''' (<see cref="ReflectionTypeLoadException"/>) still contributes its
    ''' successfully loaded types.
    ''' </summary>
    Private Function publicTypes(asm As Assembly, assemblyPath As String, warnings As List(Of String)) As Dictionary(Of String, Type)
        Dim result As New Dictionary(Of String, Type)(StringComparer.OrdinalIgnoreCase)
        Dim types As Type() = Nothing

        Try
            types = asm.GetTypes()
        Catch ex As ReflectionTypeLoadException
            types = ex.Types.Where(Function(t) t IsNot Nothing).ToArray

            Dim loaderMessages = ex.LoaderExceptions _
                .Where(Function(e) e IsNot Nothing) _
                .Select(Function(e) e.Message) _
                .Distinct() _
                .Take(3)

            Call appendWarning(warnings, $"{assemblyPath}: some types could not be loaded: {String.Join("; ", loaderMessages)}")
        Catch ex As Exception
            Call appendWarning(warnings, $"{assemblyPath}: failed to read the assembly types: {ex.Message}")
            Return result
        End Try

        For Each t As Type In If(types, {})
            If t Is Nothing Then
                Continue For
            End If

            If Not (t.IsPublic OrElse t.IsNestedPublic) Then
                Continue For
            End If

            Dim fullName As String = normalizedFullName(t)

            If String.IsNullOrEmpty(fullName) Then
                Continue For
            End If

            If Not result.ContainsKey(fullName) Then
                result(fullName) = t
            End If
        Next

        Return result
    End Function

    ''' <summary>
    ''' the xml comment document uses a dot as the nested type separator while the
    ''' reflection full name uses a plus sign.
    ''' </summary>
    Private Function normalizedFullName(t As Type) As String
        Dim name As String = t.FullName

        If String.IsNullOrEmpty(name) Then
            Return Nothing
        End If

        Return name.Replace("+"c, "."c)
    End Function

    Private Sub supplementType(entry As ApiDocType, src As Type)
        If entry.members Is Nothing Then
            entry.members = New List(Of ApiDocMember)
        End If

        Dim existing As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)

        For Each m As ApiDocMember In entry.members
            Call existing.Add(memberKey(m.kindChar, m.name, parameterCount(m.declaration)))
        Next

        Const flags As BindingFlags = BindingFlags.Public Or BindingFlags.Instance Or BindingFlags.Static Or BindingFlags.DeclaredOnly

        ' the enum members (and the other public constants) are public static
        ' literal fields which carry no parameter.
        For Each f As FieldInfo In src.GetFields(flags)
            If f Is Nothing OrElse f.IsSpecialName Then
                Continue For
            End If

            If Not addMember(existing, "F", f.Name, -1) Then
                Continue For
            End If

            Call entry.members.Add(newMember(entry, "F"c, "field", f.Name, If(f.IsLiteral, entry.fullName & "." & f.Name, entry.fullName & "." & f.Name)))
        Next

        For Each p As PropertyInfo In src.GetProperties(flags)
            If p Is Nothing OrElse p.IsSpecialName Then
                Continue For
            End If

            Dim count As Integer = p.GetIndexParameters().Length

            If Not addMember(existing, "P", p.Name, count) Then
                Continue For
            End If

            Call entry.members.Add(newMember(entry, "P"c, "property", p.Name, entry.fullName & "." & p.Name))
        Next

        For Each m As MethodInfo In src.GetMethods(flags)
            If m Is Nothing Then
                Continue For
            End If

            If m.Name = ".ctor" OrElse m.Name = ".cctor" Then
                Continue For
            End If

            ' skip the property / event accessors, but keep the operator methods.
            If m.IsSpecialName AndAlso Not m.Name.StartsWith("op_", StringComparison.Ordinal) Then
                Continue For
            End If

            Dim parameters = m.GetParameters()
            Dim count As Integer = parameters.Length

            If Not addMember(existing, "M", m.Name, count) Then
                Continue For
            End If

            Dim declaration As String = entry.fullName & "." & m.Name & "(" &
                String.Join(",", parameters.Select(Function(pi) typeName(pi.ParameterType))) & ")"

            Call entry.members.Add(newMember(entry, "M"c, "method", m.Name, declaration))
        Next

        For Each e As EventInfo In src.GetEvents(flags)
            If e Is Nothing OrElse e.IsSpecialName Then
                Continue For
            End If

            If Not addMember(existing, "E", e.Name, -1) Then
                Continue For
            End If

            Call entry.members.Add(newMember(entry, "E"c, "event", e.Name, entry.fullName & "." & e.Name))
        Next
    End Sub

    Private Function newMember(entry As ApiDocType, kindChar As Char, kind As String, name As String, declaration As String) As ApiDocMember
        Return New ApiDocMember With {
            .kindChar = kindChar.ToString,
            .kind = kind,
            .name = name,
            .declaration = declaration,
            .overloadIndex = 0,
            .parameters = New List(Of ApiDocParam),
            .typeParams = New List(Of ApiDocParam)
        }
    End Function

    ''' <summary>
    ''' test whether a member is already documented and remember the new key.
    ''' </summary>
    Private Function addMember(existing As HashSet(Of String), kindChar As String, name As String, count As Integer) As Boolean
        Dim key$ = memberKey(kindChar, name, count)
        Return existing.Add(key)
    End Function

    Private Function memberKey(kindChar As String, name As String, count As Integer) As String
        Return If(kindChar, "") & "|" & If(name, "").ToLowerInvariant & "|" & count.ToString
    End Function

    ''' <summary>
    ''' the parameter count of a xml comment declare text; -1 means unknown.
    ''' </summary>
    Private Function parameterCount(declaration As String) As Integer
        Dim types = DocNaming.ParseParameterTypes(declaration)
        Return types.Length
    End Function

    ''' <summary>
    ''' the display name of a reflected clr type, the generic type is rendered in
    ''' the ``Name{Arg1,Arg2}`` form which is aligned with the xml comment declare.
    ''' </summary>
    Private Function typeName(t As Type) As String
        If t Is Nothing Then
            Return ""
        End If

        If t.IsByRef Then
            Return typeName(t.GetElementType())
        End If

        If t.IsArray Then
            Return typeName(t.GetElementType()) & "[]"
        End If

        If t.IsGenericParameter Then
            Return t.Name
        End If

        If t.IsGenericType Then
            Dim definition As Type = t.GetGenericTypeDefinition()
            Dim baseName As String = If(definition.FullName, definition.Name)
            Dim p As Integer = baseName.IndexOf("`"c)

            If p > 0 Then
                baseName = baseName.Substring(0, p)
            End If

            Return baseName & "{" & String.Join(",", t.GetGenericArguments().Select(AddressOf typeName)) & "}"
        End If

        Return If(t.FullName, t.Name)
    End Function

    Private Sub appendWarning(warnings As List(Of String), message As String)
        If warnings IsNot Nothing Then
            Call warnings.Add("reflection: " & message)
        End If
    End Sub
End Module
