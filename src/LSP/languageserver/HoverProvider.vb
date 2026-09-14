Imports System.Text
Imports System.Text.Json.Nodes
Imports Readership

''' <summary>
''' computes the hover (类型信息提示) for a textDocument/hover request. it shows
''' the signature and documentation of either a type (when the cursor is on a type
''' name) or a member (when the cursor is on <c>Type.Member</c>).
''' </summary>
Public Class HoverProvider

    Private ReadOnly index As ApiIndex

    Public Sub New(index As ApiIndex)
        Me.index = index
    End Sub

    ''' <summary>
    ''' handle a textDocument/hover request. returns the LSP hover object, or
    ''' <c>Nothing</c> when nothing useful can be shown.
    ''' </summary>
    Public Function Provide(request As JsonNode, documents As Dictionary(Of String, String)) As JsonNode
        Dim uri As String = JsonRpc.Str(request, "textDocument", "uri")
        Dim line As Integer = JsonRpc.Int(request, "position", "line")
        Dim character As Integer = JsonRpc.Int(request, "position", "character")

        Dim text As String = ""
        If Not documents.TryGetValue(uri, text) Then
            text = ""
        End If

        Dim lineText As String = ScriptAnalyzer.GetLine(text, line)
        Dim ctx As ScriptAnalyzer.HoverContext = ScriptAnalyzer.ParseHover(lineText, character)

        If String.IsNullOrEmpty(ctx.word) Then
            Return Nothing
        End If

        Dim markdown As String = Resolve(ctx)

        If String.IsNullOrEmpty(markdown) Then
            Return Nothing
        End If

        Dim range As New JsonObject From {
            {"start", New JsonObject From {{"line", JsonValue.Create(line)}, {"character", JsonValue.Create(ctx.wordStart)}}},
            {"end", New JsonObject From {{"line", JsonValue.Create(line)}, {"character", JsonValue.Create(ctx.wordStart + ctx.word.Length)}}}
        }

        Return New JsonObject From {
            {"contents", New JsonObject From {{"kind", JsonValue.Create("markdown")}, {"value", JsonValue.Create(markdown)}}},
            {"range", range}
        }
    End Function

    ''' <summary>
    ''' resolve the markdown documentation for the hover context. returns an empty
    ''' string when the symbol can not be mapped to the api document database
    ''' (for example an instance variable, which we can not resolve without a
    ''' compiler).
    ''' </summary>
    Private Function Resolve(ctx As ScriptAnalyzer.HoverContext) As String
        If Not String.IsNullOrEmpty(ctx.container) Then
            ' the symbol could be a nested type (container.word) or a member of the
            ' container type. try the combined type name first.
            Dim fullType As String = ctx.container & "." & ctx.word
            Dim fullEntry As TypeEntry = index.FindType(fullType)

            If fullEntry IsNot Nothing Then
                Return RenderType(fullEntry)
            End If

            Dim typeEntry As TypeEntry = index.FindType(ctx.container)

            If typeEntry Is Nothing Then
                typeEntry = index.FindTypeByName(ctx.container)
            End If

            If typeEntry Is Nothing Then
                Return ""
            End If

            ' a member of the container type is being inspected
            Dim members As List(Of ApiDocMember) = index.GetMembers(typeEntry.type_fullname)
            Dim member As ApiDocMember = FindMember(members, ctx.word)

            If member IsNot Nothing Then
                Return RenderMember(typeEntry.type_fullname, member)
            End If

            ' otherwise show the container type itself
            Return RenderType(typeEntry)
        End If

        ' plain type reference: try the full name, then the short name
        Dim entry As TypeEntry = index.FindType(ctx.word)

        If entry Is Nothing Then
            entry = index.FindTypeByName(ctx.word)
        End If

        If entry IsNot Nothing Then
            Return RenderType(entry)
        End If

        Return ""
    End Function

    Private Function FindMember(members As List(Of ApiDocMember), name As String) As ApiDocMember
        If members Is Nothing Then
            Return Nothing
        End If

        For Each m As ApiDocMember In members
            If m IsNot Nothing AndAlso String.Equals(m.name, name, StringComparison.OrdinalIgnoreCase) Then
                Return m
            End If
        Next

        Return Nothing
    End Function

    Private Function RenderType(entry As TypeEntry) As String
        Dim sb As New StringBuilder()
        sb.AppendLine("### " & entry.type_fullname)
        sb.AppendLine()

        If Not String.IsNullOrEmpty(entry.summary) Then
            sb.AppendLine(entry.summary.Trim())
            sb.AppendLine()
        End If

        If Not String.IsNullOrEmpty(entry.namespaceName) Then
            sb.AppendLine("**Namespace:** " & entry.namespaceName)
        End If

        If Not String.IsNullOrEmpty(entry.packageId) Then
            sb.AppendLine("**Package:** " & entry.packageId & " " & entry.version)
        End If

        Return sb.ToString().Trim()
    End Function

    Private Function RenderMember(typeFullName As String, m As ApiDocMember) As String
        Dim sb As New StringBuilder()
        sb.AppendLine("### " & typeFullName & "." & m.name)
        sb.AppendLine()
        sb.AppendLine("```vb")
        sb.AppendLine(If(m.declaration, m.name))
        sb.AppendLine("```")
        sb.AppendLine()

        If Not String.IsNullOrEmpty(m.summary) Then
            sb.AppendLine(m.summary.Trim())
            sb.AppendLine()
        End If

        If Not String.IsNullOrEmpty(m.returns) Then
            sb.AppendLine("**Returns:** " & m.returns.Trim())
        End If

        Return sb.ToString().Trim()
    End Function
End Class
