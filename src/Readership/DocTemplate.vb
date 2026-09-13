Imports System.Text
Imports System.Text.RegularExpressions

''' <summary>
''' A minimal ``{{placeholder}}`` html template renderer. The nuget document
''' pages are rendered on the server side from the replaceable template files
''' that live in the ``template`` directory, so that the page layout could be
''' changed without a rebuild.
''' </summary>
Public Module DocTemplate

    Private ReadOnly token As New Regex("\{\{\s*(?<key>[A-Za-z0-9_.\-]+)\s*\}\}", RegexOptions.Compiled)

    ''' <summary>
    ''' render the template: every ``{{key}}`` token is replaced by its value, and
    ''' an unknown token is replaced by an empty text.
    ''' </summary>
    ''' <param name="template"></param>
    ''' <param name="values"></param>
    ''' <returns></returns>
    Public Function Render(template As String, values As IDictionary(Of String, String)) As String
        If String.IsNullOrEmpty(template) Then
            Return ""
        End If

        If values Is Nothing Then
            values = New Dictionary(Of String, String)
        End If

        Return token.Replace(template, Function(m As Match)
                                          Dim key$ = m.Groups("key").Value
                                          Dim value As String = Nothing

                                          If values.TryGetValue(key, value) AndAlso value IsNot Nothing Then
                                              Return value
                                          End If

                                          Return ""
                                      End Function)
    End Function

    ''' <summary>
    ''' the names of all of the placeholders that the given template declares
    ''' </summary>
    ''' <param name="template"></param>
    ''' <returns></returns>
    Public Function Placeholders(template As String) As String()
        If String.IsNullOrEmpty(template) Then
            Return {}
        End If

        Return token _
            .Matches(template) _
            .Select(Function(m) m.Groups("key").Value) _
            .Distinct(StringComparer.OrdinalIgnoreCase) _
            .ToArray
    End Function
End Module
