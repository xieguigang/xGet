Imports System.Collections.Generic
Imports System.Text.RegularExpressions

''' <summary>
''' best effort, compiler free extraction of local variable -&gt; declared type
''' bindings from a vb script document. this lets completion and hover resolve
''' "variable.Member" even though the server has no real type inference.
'''
''' the supported declaration forms are:
'''   Dim / Static / Const / ReadOnly  x As T
'''   Dim / Static / Const / ReadOnly  x, y As T          (multiple names)
'''   Dim / Using  x As New T(...)
'''   Dim / Using  x = New T(...)
'''   For / For Each  x As T
'''   Using  x As T
'''   Catch  x As T
'''   Sub / Function  F(x As T, y As T)                   (parameters)
'''
''' generic type arguments (e.g. List(Of String)) are reduced to the open generic
''' name (List); the index lookup is best effort for those.
''' </summary>
Public Module VariableResolver

    Private ReadOnly declRegex As New Regex( _
        "(?:Dim|Static|Const|ReadOnly|Using|For\s+Each|For|Catch)\s+([^=]+?)\s+As\s+(?:New\s+)?([\w.]+)", _
        RegexOptions.IgnoreCase Or RegexOptions.Compiled)

    Private ReadOnly newRegex As New Regex( _
        "(?:Dim|Using)\s+([\w.]+)\s*=\s*New\s+([\w.]+)", _
        RegexOptions.IgnoreCase Or RegexOptions.Compiled)

    Private ReadOnly paramRegex As New Regex( _
        "(\w+)\s+As\s+([\w.]+)", _
        RegexOptions.IgnoreCase Or RegexOptions.Compiled)

    Private ReadOnly keywords As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase) From {
        "Dim", "Static", "Const", "ReadOnly", "Using", "For", "Each", "Catch",
        "Sub", "Function", "Property", "Class", "Module", "Structure", "Enum",
        "If", "Then", "While", "Do", "Loop", "Select", "Case", "With", "End",
        "As", "New", "Of", "In", "Is", "Not", "And", "Or", "ByVal", "ByRef",
        "Public", "Private", "Friend", "Protected", "Shared", "Overrides", "Overridable"
    }

    ''' <summary>return the declared full type name for a variable, or "" when unknown.</summary>
    Public Function ResolveType(text As String, variableName As String) As String
        If String.IsNullOrEmpty(variableName) Then
            Return ""
        End If

        Dim map As Dictionary(Of String, String) = Build(text)
        Dim t As String = Nothing

        If map.TryGetValue(variableName, t) Then
            Return t
        End If

        Return ""
    End Function

    Private Function Build(text As String) As Dictionary(Of String, String)
        Dim map As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)

        If String.IsNullOrEmpty(text) Then
            Return map
        End If

        For Each rawLine As String In ScriptAnalyzer.GetLines(text)
            Dim line As String = rawLine.Trim()

            If line.Length = 0 OrElse line.StartsWith("'", StringComparison.Ordinal) Then
                Continue For
            End If

            ' form 1:  <keyword> name[s] As [New] Type
            Dim m As Match = declRegex.Match(line)

            If m.Success Then
                Dim namesPart As String = m.Groups(1).Value.Trim()
                Dim typeTok As String = m.Groups(2).Value.Trim()

                For Each nm As String In namesPart.Split(","c)
                    Dim n As String = nm.Trim()

                    If n.Length > 0 AndAlso Char.IsLetter(n(0)) Then
                        map(n) = typeTok
                    End If
                Next

                Continue For
            End If

            ' form 2:  Dim / Using name = New Type(...)
            Dim nw As Match = newRegex.Match(line)

            If nw.Success Then
                map(nw.Groups(1).Value.Trim()) = nw.Groups(2).Value.Trim()
                Continue For
            End If

            ' form 3:  parameters / any remaining "name As Type"
            For Each pm As Match In paramRegex.Matches(line)
                Dim nm As String = pm.Groups(1).Value

                If Not keywords.Contains(nm) Then
                    map(nm) = pm.Groups(2).Value.Trim()
                End If
            Next
        Next

        Return map
    End Function
End Module
