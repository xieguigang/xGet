Imports System.IO
Imports System.Reflection
Imports System.Runtime.InteropServices
Imports System.Runtime.Loader

''' <summary>
''' The reflection based supplement of the api reference document. The xml
''' comment document that the compiler generates only contains the members that
''' actually carry a comment: for example the members of an enum which have no
''' xml comment are simply absent. This module reflects the sibling clr assembly
''' of a comment document and appends the missing members (public only) to the
''' document model, with an empty comment text.
''' 
''' The assemblies are loaded into a <see cref="MetadataLoadContext"/> which
''' reads the clr metadata only: no module initializer, no type initializer and
''' no custom attribute constructor of the inspected assembly is ever executed,
''' so a hostile uploaded assembly can not run any code inside this process.
''' The <see cref="PathAssemblyResolver"/> is restricted to the runtime library
''' folder and the package folder (a whitelist), so the inspected assembly can
''' not pull in arbitrary assemblies from the application directory either.
''' </summary>
Public Module DocReflection

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

        Dim targets As New List(Of String)

        For Each xml As String In xmlDocuments
            Dim assemblyPath As String = Path.ChangeExtension(xml, ".dll")

            If File.Exists(assemblyPath) Then
                Call targets.Add(assemblyPath)
            End If
        Next

        If targets.Count = 0 Then
            Return
        End If

        Using context As MetadataLoadContext = createMetadataContext(targets, warnings)
            Dim cache As New Dictionary(Of String, Assembly)(StringComparer.OrdinalIgnoreCase)

            For Each assemblyPath As String In targets
                Dim asm As Assembly = loadAssembly(context, assemblyPath, cache, warnings)

                If asm Is Nothing Then
                    Continue For
                End If

                Call supplementAssembly(document, asm, assemblyPath, warnings)
            Next
        End Using
    End Sub

    ''' <summary>
    ''' build the metadata load context. the resolver whitelist contains every
    ''' managed library of the runtime folder plus every managed library that
    ''' sits next to the inspected assemblies (the extracted package content),
    ''' so the dependency resolution of the inspected assemblies stays inside
    ''' this closed set and can never fall back to the application directory.
    ''' </summary>
    Private Function createMetadataContext(assemblyPaths As IEnumerable(Of String), warnings As List(Of String)) As MetadataLoadContext
        Dim probe As New List(Of String)(StringComparer.OrdinalIgnoreCase)

        ' the core runtime libraries: they satisfy the well known framework
        ' references (System.Runtime, netstandard, System.Private.CoreLib, ...)
        ' of almost every managed assembly.
        Try
            Dim runtimeDirectory As String = RuntimeEnvironment.GetRuntimeDirectory()

            If Directory.Exists(runtimeDirectory) Then
                For Each file As String In Directory.GetFiles(runtimeDirectory, "*.dll")
                    Call probe.Add(file)
                Next
            End If
        Catch ex As Exception
            Call appendWarning(warnings, $"the runtime folder could not be probed: {ex.Message}")
        End Try

        ' the inspected assemblies and their package local dependencies
        For Each assemblyPath As String In assemblyPaths
            Dim folder As String = Path.GetDirectoryName(Path.GetFullPath(assemblyPath))

            If Not String.IsNullOrEmpty(folder) AndAlso Directory.Exists(folder) Then
                For Each file As String In Directory.GetFiles(folder, "*.dll")
                    Call probe.Add(file)
                Next
            End If

            If File.Exists(assemblyPath) Then
                Call probe.Add(assemblyPath)
            End If
        Next

        Dim resolver As New PathAssemblyResolver(probe.Distinct(StringComparer.OrdinalIgnoreCase))
        Return New MetadataLoadContext(resolver)
    End Function

    Private Function loadAssembly(context As MetadataLoadContext, assemblyPath As String,
                                  cache As Dictionary(Of String, Assembly), warnings As List(Of String)) As Assembly

        Dim cached As Assembly = Nothing

        If cache.TryGetValue(assemblyPath, cached) Then
            Return cached
        End If

        Try
            Dim asm As Assembly = context.LoadFromAssemblyPath(Path.GetFullPath(assemblyPath))
            cache(assemblyPath) = asm
            Return asm
        Catch ex As Exception
            Call appendWarning(warnings, $"failed to load the assembly '{assemblyPath}': {ex.Message}")
            Return Nothing
        End Try
    End Function

    Private Sub supplementAssembly(document As ApiDocDocument, asm As Assembly, assemblyPath As String, warnings As List(Of String))
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

            Call entry.members.Add(newMember(entry, "F"c, "field", f.Name, entry.fullName & "." & f.Name))
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