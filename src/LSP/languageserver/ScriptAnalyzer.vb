Imports System.Text

''' <summary>
''' light weight, compiler free text heuristics used to derive the completion /
''' hover / signature context at the cursor. there is deliberately no roslyn or
''' syntax tree: the server is scoped to provide intellisense only, never to
''' compile or to validate the script.
''' </summary>
Public Module ScriptAnalyzer

    ''' <summary>characters that may appear inside a vb identifier.</summary>
    Private Function IsIdentChar(c As Char) As Boolean
        Return Char.IsLetterOrDigit(c) OrElse c = "_"c
    End Function

    ''' <summary>split a document text into its physical lines (lf normalized).</summary>
    Public Function GetLines(text As String) As String()
        If String.IsNullOrEmpty(text) Then
            Return New String() {}
        End If

        Return text.Replace(vbCrLf, vbLf).Split(vbLf)
    End Function

    ''' <summary>return the text of a single line, or an empty string when out of range.</summary>
    Public Function GetLine(text As String, line As Integer) As String
        Dim lines As String() = GetLines(text)

        If line >= 0 AndAlso line < lines.Length Then
            Return lines(line)
        End If

        Return ""
    End Function

    ''' <summary>the portion of the line from its start up to the cursor column.</summary>
    Public Function GetLinePrefix(text As String, line As Integer, character As Integer) As String
        Dim lineText As String = GetLine(text, line)

        If character < 0 Then
            Return ""
        End If

        If character > lineText.Length Then
            character = lineText.Length
        End If

        Return lineText.Substring(0, character)
    End Function

    ''' <summary>the identifier under the cursor, together with its start column.</summary>
    Public Class WordAt
        Public Property word As String = ""
        Public Property start As Integer = 0
    End Class

    ''' <summary>
    ''' extract the identifier surrounding <paramref name="character"/> on the
    ''' given line. when the cursor is between characters, the word before it is
    ''' preferred so that hovering a member still works mid typing.
    ''' </summary>
    Public Function GetWordAt(line As String, character As Integer) As WordAt
        Dim result As New WordAt()

        If String.IsNullOrEmpty(line) Then
            Return result
        End If

        If character > line.Length Then
            character = line.Length
        End If

        ' scan left from the cursor to find the start of the word
        Dim startPos As Integer = character
        While startPos > 0 AndAlso IsIdentChar(line(startPos - 1))
            startPos -= 1
        End While

        ' scan right from the cursor to find the end of the word
        Dim endPos As Integer = character
        While endPos < line.Length AndAlso IsIdentChar(line(endPos))
            endPos += 1
        End While

        result.start = startPos
        result.word = line.Substring(startPos, endPos - startPos)
        Return result
    End Function

    ''' <summary>the parsed completion context at the cursor.</summary>
    Public Class CompletionContext
        ''' <summary>the package / namespace / type qualifier before the last dot (empty for top level).</summary>
        Public Property container As String = ""

        ''' <summary>the identifier fragment currently being typed (after the last dot).</summary>
        Public Property fragment As String = ""

        ''' <summary>true when the line is an <c>Imports</c> statement.</summary>
        Public Property isImports As Boolean = False

        ''' <summary>true when the cursor sits directly after a dot (so member/segment completion is wanted).</summary>
        Public Property afterDot As Boolean = False

        ''' <summary>the original line prefix the context was parsed from.</summary>
        Public Property linePrefix As String = ""
    End Class

    ''' <summary>
    ''' parse the text prefix before the cursor into a completion context. the
    ''' trailing dotted identifier chain (ignoring any leading keywords such as
    ''' <c>Dim</c>/<c>New</c>/<c>As</c>) is split into a container and the fragment
    ''' fragment that is being typed.
    ''' </summary>
    Public Function ParseCompletion(linePrefix As String) As CompletionContext
        Dim ctx As New CompletionContext()
        ctx.linePrefix = linePrefix

        Dim trimmed As String = linePrefix.TrimStart()
        If trimmed.StartsWith("Imports", StringComparison.OrdinalIgnoreCase) AndAlso
           (trimmed.Length = "Imports".Length OrElse Char.IsWhiteSpace(trimmed("Imports".Length))) Then
            ctx.isImports = True
        End If

        ' the trailing run of identifier / dot characters is the relevant qualifier;
        ' anything before it (keywords, operators) is irrelevant for completion.
        Dim chain As String = TrailingQualifier(linePrefix)

        If String.IsNullOrEmpty(chain) Then
            Return ctx
        End If

        If chain.EndsWith("."c) Then
            ctx.afterDot = True
            ctx.container = chain.Substring(0, chain.Length - 1)
            ctx.fragment = ""
        Else
            Dim lastDot As Integer = chain.LastIndexOf("."c)

            If lastDot < 0 Then
                ctx.container = ""
                ctx.fragment = chain
            Else
                ctx.container = chain.Substring(0, lastDot)
                ctx.fragment = chain.Substring(lastDot + 1)
                ctx.afterDot = True
            End If
        End If

        Return ctx
    End Function

    ''' <summary>
    ''' return the longest run of identifier / dot characters at the end of the
    ''' text; stops at the first invalid character (space, parenthesis, operator).
    ''' </summary>
    Private Function TrailingQualifier(text As String) As String
        If String.IsNullOrEmpty(text) Then
            Return ""
        End If

        Dim endPos As Integer = text.Length
        While endPos > 0 AndAlso (IsIdentChar(text(endPos - 1)) OrElse text(endPos - 1) = "."c)
            endPos -= 1
        End While

        Return text.Substring(endPos)
    End Function

    ''' <summary>the parsed hover context at the cursor.</summary>
    Public Class HoverContext
        ''' <summary>the word under the cursor (a type or member name).</summary>
        Public Property word As String = ""

        ''' <summary>the start column of <see cref="word"/> on the line (for range highlighting).</summary>
        Public Property wordStart As Integer = 0

        ''' <summary>
        ''' the qualifier before a dot that precedes the word: empty for a plain
        ''' type reference, the owning type name for a <c>Type.Member</c> reference.
        ''' </summary>
        Public Property container As String = ""
    End Class

    ''' <summary>
    ''' compute the hover context: the word under the cursor plus, when the cursor
    ''' follows <c>container.</c>, the qualifier that names the containing type.
    ''' </summary>
    Public Function ParseHover(line As String, character As Integer) As HoverContext
        Dim ctx As New HoverContext()
        Dim word As WordAt = GetWordAt(line, character)

        ctx.word = word.word
        ctx.wordStart = word.start

        If word.start > 0 AndAlso word.start <= line.Length AndAlso line(word.start - 1) = "."c Then
            ' only keep the trailing dotted identifier chain before the dot, so that
            ' a type reference like "Dim x As New System.Text.StringBuilder" resolves
            ' to the container "System.Text" rather than the leading keywords.
            ctx.container = TrailingQualifier(line.Substring(0, word.start - 1)).Trim()
        End If

        Return ctx
    End Function

    ''' <summary>the parsed signature help context at the cursor.</summary>
    Public Class SignatureContext
        ''' <summary>the type or expression that owns the method being called.</summary>
        Public Property container As String = ""

        ''' <summary>the method name whose overloads are being shown.</summary>
        Public Property method As String = ""

        ''' <summary>the zero based index of the parameter being typed.</summary>
        Public Property activeParameter As Integer = 0
    End Class

    ''' <summary>
    ''' compute the signature help context from the line prefix. finds the last
    ''' open parenthesis, reads the call expression that precedes it, and counts
    ''' the top level commas to know which parameter is active.
    ''' </summary>
    Public Function ParseSignature(linePrefix As String) As SignatureContext
        Dim ctx As New SignatureContext()
        Dim openParen As Integer = linePrefix.LastIndexOf("("c)

        If openParen < 0 Then
            Return Nothing
        End If

        Dim expression As String = linePrefix.Substring(0, openParen)
        Dim chain As String = TrailingQualifier(expression)

        If Not String.IsNullOrEmpty(chain) Then
            Dim lastDot As Integer = chain.LastIndexOf("."c)

            If lastDot < 0 Then
                ctx.container = ""
                ctx.method = chain
            Else
                ctx.container = chain.Substring(0, lastDot)
                ctx.method = chain.Substring(lastDot + 1)
            End If
        End If

        ' count the commas that sit between the open parenthesis and the cursor,
        ' ignoring those nested inside inner method calls.
        Dim inside As String = linePrefix.Substring(openParen + 1)
        Dim depth As Integer = 0

        For Each ch As Char In inside
            If ch = "("c Then
                depth += 1
            ElseIf ch = ")"c Then
                depth -= 1
            ElseIf ch = ","c AndAlso depth = 0 Then
                ctx.activeParameter += 1
            End If
        Next

        Return ctx
    End Function
End Module
