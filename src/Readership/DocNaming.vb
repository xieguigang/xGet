Imports System.Text
Imports Microsoft.VisualBasic.ApplicationServices
Imports Microsoft.VisualBasic.Linq

''' <summary>
''' The naming and formatting helpers of the api reference document site. These
''' helpers are pure functions which do not depend on a specific document
''' instance, so that they could be shared by the document extractor, the
''' offline static site renderer and the nuget server side renderer.
''' </summary>
Public Module DocNaming

    ''' <summary>
    ''' convert a namespace / type / member name into a safe file name fragment
    ''' </summary>
    ''' <param name="text"></param>
    ''' <returns></returns>
    Public Function Slug(text As String) As String
        Dim sb As New StringBuilder

        For Each c As Char In If(text, "")
            If Char.IsLetterOrDigit(c) OrElse c = "."c OrElse c = "_"c OrElse c = "-"c Then
                sb.Append(c)
            Else
                sb.Append("_"c)
            End If
        Next

        Dim s$ = sb.ToString.TrimEnd("."c, " "c)

        If String.IsNullOrEmpty(s) Then
            s = "unnamed"
        End If

        If s.Length > 100 Then
            s = s.Substring(0, 100) & "-" & StableHash(text)
        End If

        Return s
    End Function

    ''' <summary>
    ''' a process independent string hash, the <see cref="String.GetHashCode"/> is
    ''' randomized between the process runs so it could not be used for the
    ''' stable file naming.
    ''' </summary>
    ''' <param name="text"></param>
    ''' <returns></returns>
    Public Function StableHash(text As String) As String
        Dim h As Long = 17

        For Each c As Char In If(text, "")
            h = (h * 31 + AscW(c)) Mod 1000000007L
        Next

        Return h.ToString("x")
    End Function

    ''' <summary>
    ''' split the parameter type list from a member declare text, for example
    ''' ``Ns.Type.Method(System.String,System.Int32)`` =&gt;
    ''' ``{System.String, System.Int32}``.
    ''' </summary>
    ''' <param name="declareText"></param>
    ''' <returns></returns>
    Public Function ParseParameterTypes(declareText As String) As String()
        If String.IsNullOrWhiteSpace(declareText) Then
            Return {}
        End If

        Dim openIndex As Integer = declareText.IndexOf("("c)

        If openIndex < 0 Then
            Return {}
        End If

        Dim closeIndex As Integer = declareText.LastIndexOf(")"c)

        If closeIndex <= openIndex + 1 Then
            Return {}
        End If

        Return SplitTopLevel(declareText.Substring(openIndex + 1, closeIndex - openIndex - 1))
    End Function

    ''' <summary>
    ''' convert a doc id type reference into a ``cref`` identity, the array /
    ''' byref / pointer suffix and the generic arguments are handled, for example
    ''' ``System.Collections.Generic.Dictionary{System.String,System.Object}``
    ''' =&gt; ``T:System.Collections.Generic.Dictionary`2``.
    ''' </summary>
    ''' <param name="typeReference"></param>
    ''' <returns></returns>
    Public Function TypeCref(typeReference As String) As String
        Dim name$ = StripTypeSuffix(typeReference)

        If name.Length = 0 Then
            Return Nothing
        End If

        Dim openIndex As Integer = name.IndexOf("{"c)

        If openIndex > 0 Then
            name = name.Substring(0, openIndex) & "`" & CountGenericArguments(name, openIndex).ToString
        End If

        Return "T:" & name
    End Function

    ''' <summary>
    ''' the vb style display text of a doc id type reference, for example
    ''' ``Dictionary{System.String,System.Object}`` =&gt;
    ''' ``Dictionary(Of String, Object)`` and ``System.String[]`` =&gt; ``String()``.
    ''' </summary>
    ''' <param name="typeReference"></param>
    ''' <returns></returns>
    Public Function DisplayTypeReference(typeReference As String) As String
        Dim name$ = If(typeReference, "").Trim()

        If name.Length = 0 Then
            Return ""
        End If

        If name.EndsWith("[]", StringComparison.Ordinal) Then
            Return DisplayTypeReference(name.Substring(0, name.Length - 2)) & "()"
        End If

        If name.EndsWith("*", StringComparison.Ordinal) OrElse name.EndsWith("@"c) Then
            Return DisplayTypeReference(name.Substring(0, name.Length - 1))
        End If

        Dim openIndex As Integer = name.IndexOf("{"c)

        If openIndex > 0 AndAlso name.EndsWith("}", StringComparison.Ordinal) Then
            Dim arguments$ = name.Substring(openIndex + 1, name.Length - openIndex - 2)
            Dim parts = SplitTopLevel(arguments)

            Return ShortTypeName(name.Substring(0, openIndex)) &
                "(Of " & parts.Select(AddressOf DisplayTypeReference).JoinBy(", ") & ")"
        End If

        Return ShortTypeName(name)
    End Function

    ''' <summary>
    ''' the short name of a clr type name, the generic arity suffix and the
    ''' explicit interface separator are removed.
    ''' </summary>
    ''' <param name="name"></param>
    ''' <returns></returns>
    Public Function ShortTypeName(name As String) As String
        Dim bare$ = If(name, "").Trim()
        Dim p As Integer = bare.LastIndexOf("."c)

        If p >= 0 Then
            bare = bare.Substring(p + 1)
        End If

        p = bare.IndexOf("`"c)

        If p > 0 Then
            bare = bare.Substring(0, p)
        End If

        If bare.Contains("#") Then
            bare = bare.Replace("#", ".")
        End If

        Return bare
    End Function

    ''' <summary>
    ''' remove the parameter list of a member declare text
    ''' </summary>
    ''' <param name="name"></param>
    ''' <returns></returns>
    Public Function StripParams(name As String) As String
        If String.IsNullOrEmpty(name) Then
            Return ""
        End If

        Dim p = name.IndexOf("("c)

        If p > 0 Then
            name = name.Substring(0, p)
        End If

        Return name
    End Function

    ''' <summary>
    ''' the folder path of a namespace. it is built from the namespace segments so
    ''' that the generated pages are distributed into the hierarchical sub
    ''' directories. the global namespace uses the ``_global`` folder.
    ''' </summary>
    ''' <param name="namespaceName"></param>
    ''' <returns></returns>
    Public Function NamespaceFolder(namespaceName As String) As String
        If String.IsNullOrWhiteSpace(namespaceName) Then
            Return "_global"
        End If

        Return namespaceName _
            .Split("."c) _
            .Where(Function(s) Not String.IsNullOrWhiteSpace(s)) _
            .Select(Function(s) Slug(s)) _
            .JoinBy("/")
    End Function

    ''' <summary>
    ''' the html anchor of a namespace block inside a document index page, it is
    ''' used by the sidebar navigation of the server side document pages (which
    ''' have no dedicated namespace page).
    ''' </summary>
    ''' <param name="namespaceName"></param>
    ''' <returns></returns>
    Public Function NamespaceAnchor(namespaceName As String) As String
        Return "ns-" & NamespaceFolder(namespaceName).Replace("/"c, "-"c)
    End Function

    ''' <summary>
    ''' the full namespace name of a namespace tree node, it is built by walking
    ''' up the <see cref="FileSystemTree.Parent"/> chain.
    ''' </summary>
    ''' <param name="node"></param>
    ''' <returns></returns>
    Public Function NodeFullName(node As FileSystemTree) As String
        Dim names As New List(Of String)
        Dim cur As FileSystemTree = node

        While cur IsNot Nothing AndAlso Not String.IsNullOrEmpty(cur.Name)
            Call names.Insert(0, cur.Name)
            cur = cur.Parent
        End While

        Return names.JoinBy(".")
    End Function

    ''' <summary>
    ''' split a comma separated list by the top level comma, the comma inside the
    ''' generic arguments or the nested braces is not a separator.
    ''' </summary>
    ''' <param name="text"></param>
    ''' <returns></returns>
    Public Function SplitTopLevel(text As String) As String()
        Dim parts As New List(Of String)
        Dim depth As Integer = 0
        Dim token As New StringBuilder

        For Each c As Char In If(text, "")
            Select Case c
                Case "{"c, "("c, "["c
                    depth += 1
                    Call token.Append(c)
                Case "}"c, ")"c, "]"c
                    depth -= 1
                    Call token.Append(c)
                Case ","c
                    If depth = 0 Then
                        Call AddToken(parts, token)
                    Else
                        Call token.Append(c)
                    End If
                Case Else
                    Call token.Append(c)
            End Select
        Next

        Call AddToken(parts, token)

        Return parts.ToArray
    End Function

    Private Sub AddToken(parts As List(Of String), token As StringBuilder)
        Dim text$ = token.ToString.Trim()
        Call token.Clear()

        If text.Length > 0 Then
            Call parts.Add(text)
        End If
    End Sub

    ''' <summary>
    ''' remove the array / byref / pointer suffix of a doc id type reference
    ''' </summary>
    ''' <param name="typeReference"></param>
    ''' <returns></returns>
    Private Function StripTypeSuffix(typeReference As String) As String
        Dim name$ = If(typeReference, "").Trim()

        While name.Length > 0
            If name.EndsWith("[]", StringComparison.Ordinal) Then
                name = name.Substring(0, name.Length - 2).Trim()
            ElseIf name.EndsWith("*", StringComparison.Ordinal) OrElse name.EndsWith("@"c) Then
                name = name.Substring(0, name.Length - 1).Trim()
            Else
                Exit While
            End If
        End While

        Return name
    End Function

    ''' <summary>
    ''' count the top level arguments of the first generic argument group
    ''' </summary>
    ''' <param name="name"></param>
    ''' <param name="openIndex"></param>
    ''' <returns></returns>
    Private Function CountGenericArguments(name As String, openIndex As Integer) As Integer
        Dim depth As Integer = 0
        Dim count As Integer = 0
        Dim hasArgument As Boolean = False

        For i As Integer = openIndex To name.Length - 1
            Dim c As Char = name(i)

            Select Case c
                Case "{"c
                    depth += 1

                    If depth = 1 Then
                        hasArgument = False
                    End If
                Case "}"c
                    If depth = 1 AndAlso hasArgument Then
                        count += 1
                    End If

                    depth -= 1

                    If depth = 0 Then
                        Exit For
                    End If
                Case ","c
                    If depth = 1 Then
                        count += 1
                    End If
                Case Else
                    If depth = 1 AndAlso Not Char.IsWhiteSpace(c) Then
                        hasArgument = True
                    End If
            End Select
        Next

        Return Math.Max(count, 1)
    End Function
End Module
