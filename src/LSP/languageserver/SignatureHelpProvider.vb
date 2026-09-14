Imports System.Collections.Generic
Imports System.Text
Imports System.Text.Json.Nodes
Imports Readership

''' <summary>
''' computes the signature help (方法参数提示) for a textDocument/signatureHelp
''' request. when the cursor is inside a method call, it lists the overloads of the
''' method and highlights the parameter that is currently being typed.
''' </summary>
Public Class SignatureHelpProvider

    Private ReadOnly index As ApiIndex

    Public Sub New(index As ApiIndex)
        Me.index = index
    End Sub

    ''' <summary>
    ''' handle a textDocument/signatureHelp request. returns the LSP signature help
    ''' object, or <c>Nothing</c> when no matching method can be resolved.
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
        Dim ctx As ScriptAnalyzer.SignatureContext = ScriptAnalyzer.ParseSignature(prefix)

        If ctx Is Nothing OrElse String.IsNullOrEmpty(ctx.method) Then
            Return Nothing
        End If

        Dim typeEntry As TypeEntry = index.FindType(ctx.container)

        If typeEntry Is Nothing Then
            typeEntry = index.FindTypeByName(ctx.container)
        End If

        If typeEntry Is Nothing Then
            Return Nothing
        End If

        Dim members As List(Of ApiDocMember) = index.GetMembers(typeEntry.type_fullname)
        Dim matches As New List(Of ApiDocMember)()

        If members IsNot Nothing Then
            For Each m As ApiDocMember In members
                If m IsNot Nothing AndAlso String.Equals(m.name, ctx.method, StringComparison.OrdinalIgnoreCase) Then
                    matches.Add(m)
                End If
            Next
        End If

        If matches.Count = 0 Then
            Return Nothing
        End If

        Dim signatures As New JsonArray()

        For Each m As ApiDocMember In matches
            signatures.Add(BuildSignature(m))
        Next

        ' pick the overload whose parameter count best fits the active argument
        Dim activeSignature As Integer = 0

        For i As Integer = 0 To matches.Count - 1
            Dim count As Integer = CountParameters(matches(i).declaration)
            If count >= ctx.activeParameter + 1 Then
                activeSignature = i
                Exit For
            End If
        Next

        Return New JsonObject From {
            {"signatures", signatures},
            {"activeSignature", JsonValue.Create(activeSignature)},
            {"activeParameter", JsonValue.Create(ctx.activeParameter)}
        }
    End Function

    Private Function BuildSignature(m As ApiDocMember) As JsonObject
        Dim sig As New JsonObject()
        sig("label") = JsonValue.Create(If(m.declaration, m.name))

        If Not String.IsNullOrEmpty(m.summary) Then
            sig("documentation") = New JsonObject From {
                {"kind", JsonValue.Create("markdown")},
                {"value", JsonValue.Create(m.summary.Trim())}
            }
        End If

        Dim labels As List(Of String) = ParseParameters(If(m.declaration, ""))
        Dim paramsArr As New JsonArray()

        For i As Integer = 0 To labels.Count - 1
            Dim p As New JsonObject()
            p("label") = JsonValue.Create(labels(i))

            Dim doc As String = ""
            If m.parameters IsNot Nothing AndAlso i < m.parameters.Count AndAlso m.parameters(i) IsNot Nothing Then
                doc = m.parameters(i).text
            End If

            If Not String.IsNullOrEmpty(doc) Then
                p("documentation") = New JsonObject From {
                    {"kind", JsonValue.Create("markdown")},
                    {"value", JsonValue.Create(doc.Trim())}
                }
            End If

            paramsArr.Add(p)
        Next

        sig("parameters") = paramsArr
        Return sig
    End Function

    ''' <summary>extract the top level parameter labels from a declaration string.</summary>
    Private Function ParseParameters(declaration As String) As List(Of String)
        Dim result As New List(Of String)()
        Dim open As Integer = declaration.IndexOf("("c)

        If open < 0 Then
            Return result
        End If

        Dim close As Integer = declaration.LastIndexOf(")"c)
        If close < open Then
            close = declaration.Length
        End If

        Dim inner As String = declaration.Substring(open + 1, close - open - 1)

        If String.IsNullOrWhiteSpace(inner) Then
            Return result
        End If

        Dim depth As Integer = 0
        Dim start As Integer = 0

        For i As Integer = 0 To inner.Length - 1
            Dim ch As Char = inner(i)

            If ch = "("c Then
                depth += 1
            ElseIf ch = ")"c Then
                depth -= 1
            ElseIf ch = ","c AndAlso depth = 0 Then
                result.Add(inner.Substring(start, i - start).Trim())
                start = i + 1
            End If
        Next

        result.Add(inner.Substring(start).Trim())
        Return result
    End Function

    ''' <summary>count the number of parameters in a declaration (0 for a parameterless one).</summary>
    Private Function CountParameters(declaration As String) As Integer
        Dim labels As List(Of String) = ParseParameters(If(declaration, ""))
        Return labels.Count
    End Function
End Class
