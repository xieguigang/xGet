Imports System.Collections.Generic
Imports System.Text
Imports System.Text.Json.Nodes
Imports Readership

''' <summary>
''' computes the completion list (智能提示) for a textDocument/completion request.
''' it supports two scenarios:
''' <list type="bullet">
''' <item>top level / <c>Imports</c>: complete namespace and type names;</item>
''' <item>after a dot: complete members of a known type, or nested namespaces / types
''' of a known namespace.</item>
''' </list>
''' the resolution is purely heuristic and never compiles the script.
''' </summary>
Public Class CompletionProvider

    Private ReadOnly index As ApiIndex

    ' LSP CompletionItemKind values used below
    Private Const KindNamespace As Integer = 9
    Private Const KindClass As Integer = 7
    Private Const KindMethod As Integer = 2
    Private Const KindProperty As Integer = 10
    Private Const KindField As Integer = 5
    Private Const KindEvent As Integer = 23

    Private Const MaxItems As Integer = 200

    Public Sub New(index As ApiIndex)
        Me.index = index
    End Sub

    ''' <summary>
    ''' handle a textDocument/completion request and return the LSP result object
    ''' (a completion list). returns an empty completion list (never null) when the
    ''' document or cursor can not be resolved.
    ''' </summary>
    Public Function Provide(request As JsonNode, documents As Dictionary(Of String, String)) As JsonNode
        Dim uri As String = JsonRpc.Str(request, "textDocument", "uri")
        Dim line As Integer = JsonRpc.Int(request, "position", "line")
        Dim character As Integer = JsonRpc.Int(request, "position", "character")

        Dim text As String = ""
        If Not documents.TryGetValue(uri, text) Then
            text = ""
        End If

        Dim prefix As String = ScriptAnalyzer.GetLinePrefix(text, line, character)
        Dim ctx As ScriptAnalyzer.CompletionContext = ScriptAnalyzer.ParseCompletion(prefix)
        Dim items As List(Of JsonObject) = ComputeItems(ctx)

        Return BuildCompletionList(items)
    End Function

    Private Function ComputeItems(ctx As ScriptAnalyzer.CompletionContext) As List(Of JsonObject)
        Dim items As New List(Of JsonObject)()

        If ctx.afterDot AndAlso Not String.IsNullOrEmpty(ctx.container) Then
            ' the container is a known type: complete its members
            Dim typeEntry As TypeEntry = index.FindType(ctx.container)

            If typeEntry IsNot Nothing Then
                AddMembers(items, index.GetMembers(ctx.container), ctx.fragment)
                Return Truncate(items)
            End If

            ' otherwise treat the container as a (possibly fragment) namespace
            For Each ns As String In index.MatchNestedNamespaces(ctx.container, ctx.fragment)
                AddNamespace(items, ns)
            Next

            For Each t As TypeEntry In index.MatchTypesInNamespace(ctx.container, ctx.fragment)
                AddType(items, t)
            Next

            If items.Count = 0 Then
                ' last resort: any type whose full name continues the container path
                For Each t As TypeEntry In index.MatchTypes(ctx.container & "." & ctx.fragment)
                    AddType(items, t)
                Next
            End If

            Return Truncate(items)
        End If

        ' top level: namespaces and types whose name matches the fragment fragment
        For Each ns As String In index.MatchNamespaces(ctx.fragment)
            AddNamespace(items, ns)
        Next

        For Each t As TypeEntry In index.MatchTypes(ctx.fragment)
            AddType(items, t)
        Next

        Return Truncate(items)
    End Function

    Private Function Truncate(items As List(Of JsonObject)) As List(Of JsonObject)
        If items.Count <= MaxItems Then
            Return items
        End If

        Return items.GetRange(0, MaxItems)
    End Function

    Private Sub AddNamespace(items As List(Of JsonObject), name As String)
        items.Add(BuildItem(name, KindNamespace, "Namespace", ""))
    End Sub

    Private Sub AddType(items As List(Of JsonObject), entry As TypeEntry)
        Dim shortName As String = entry.type_fullname

        If Not String.IsNullOrEmpty(entry.namespaceName) AndAlso entry.type_fullname.Length > entry.namespaceName.Length Then
            shortName = entry.type_fullname.Substring(entry.namespaceName.Length + 1)
        End If

        Dim detail As String = entry.type_fullname
        If Not String.IsNullOrEmpty(entry.packageId) Then
            detail &= "  (in " & entry.packageId & " " & entry.version & ")"
        End If

        items.Add(BuildItem(shortName, KindClass, detail, entry.summary))
    End Sub

    Private Sub AddMembers(items As List(Of JsonObject), members As List(Of ApiDocMember), fragment As String)
        Dim lower As String = If(fragment, "").ToLowerInvariant()
        Dim groups As New Dictionary(Of String, List(Of ApiDocMember))(StringComparer.OrdinalIgnoreCase)

        If members Is Nothing Then
            Return
        End If

        For Each m As ApiDocMember In members
            If m Is Nothing OrElse String.IsNullOrEmpty(m.name) Then
                Continue For
            End If

            If Not String.IsNullOrEmpty(lower) AndAlso Not m.name.ToLowerInvariant().StartsWith(lower) Then
                Continue For
            End If

            If Not groups.ContainsKey(m.name) Then
                groups(m.name) = New List(Of ApiDocMember)()
            End If

            groups(m.name).Add(m)
        Next

        For Each kvp As KeyValuePair(Of String, List(Of ApiDocMember)) In groups
            AddMemberGroup(items, kvp.Value)
        Next
    End Sub

    Private Sub AddMemberGroup(items As List(Of JsonObject), members As List(Of ApiDocMember))
        Dim first As ApiDocMember = members(0)
        Dim kind As Integer = KindForMember(first)
        Dim detail As String = If(first.declaration, first.name)

        If members.Count > 1 Then
            detail &= "  (+" & (members.Count - 1) & " overload(s))"
        End If

        Dim doc As String = BuildMemberDoc(first)
        items.Add(BuildItem(first.name, kind, detail, doc))
    End Sub

    Private Function KindForMember(m As ApiDocMember) As Integer
        Select Case If(m.kindChar, "")
            Case "M"c : Return KindMethod
            Case "P"c : Return KindProperty
            Case "F"c : Return KindField
            Case "E"c : Return KindEvent
            Case Else : Return KindMethod
        End Select
    End Function

    Private Function BuildMemberDoc(m As ApiDocMember) As String
        Dim sb As New StringBuilder()

        If Not String.IsNullOrEmpty(m.summary) Then
            sb.AppendLine(m.summary.Trim())
        End If

        If Not String.IsNullOrEmpty(m.returns) Then
            sb.AppendLine()
            sb.AppendLine("**Returns:** " & m.returns.Trim())
        End If

        If Not String.IsNullOrEmpty(m.remarks) Then
            sb.AppendLine()
            sb.AppendLine(m.remarks.Trim())
        End If

        Return sb.ToString().Trim()
    End Function

    Private Function BuildItem(label As String, kind As Integer, detail As String, docMarkdown As String) As JsonObject
        Dim item As New JsonObject()
        item("label") = JsonValue.Create(label)
        item("kind") = JsonValue.Create(kind)

        If Not String.IsNullOrEmpty(detail) Then
            item("detail") = JsonValue.Create(detail)
        End If

        If Not String.IsNullOrEmpty(docMarkdown) Then
            item("documentation") = New JsonObject From {
                {"kind", JsonValue.Create("markdown")},
                {"value", JsonValue.Create(docMarkdown)}
            }
        End If

        item("insertText") = JsonValue.Create(label)
        Return item
    End Function

    Private Function BuildCompletionList(items As List(Of JsonObject)) As JsonObject
        Dim arr As New JsonArray()

        For Each it As JsonObject In items
            arr.Add(it)
        Next

        Return New JsonObject From {
            {"isIncomplete", JsonValue.Create(False)},
            {"items", arr}
        }
    End Function
End Class
