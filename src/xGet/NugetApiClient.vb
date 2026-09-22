Imports System.Collections.Generic
Imports System.IO
Imports System.Net.Http
Imports System.Net.Http.Headers
Imports System.Text.Json

''' <summary>
''' the parsed json response of the experimental nuget server api.
''' </summary>
Public Class ApiResult
    Public Property ok As Boolean
    Public Property message As String
    Public Property email As String
    Public Property secret As String
    Public Property id As String
    Public Property version As String
End Class

''' <summary>
''' a small http client for the experimental nuget server: register a new email
''' (which returns the TOTP secret) and upload a nupkg with a TOTP code.
''' </summary>
''' <remarks>
''' downloads are intentionally not implemented here, the official visual
''' studio nuget client is used to anonymously download packages.
''' </remarks>
Public Class NugetApiClient

    Private Shared ReadOnly JsonOptions As New JsonSerializerOptions With {
        .PropertyNameCaseInsensitive = True
    }

    Private Shared ReadOnly Http As New HttpClient() With {.Timeout = TimeSpan.FromMinutes(15)}

    Private ReadOnly baseUrl As String

    Public Sub New(server As String)
        Me.baseUrl = normalize(server)
    End Sub

    ''' <summary>
    ''' register the given email on the server and return the TOTP secret.
    ''' </summary>
    Public Function Register(email As String) As ApiResult
        Using content As New FormUrlEncodedContent(New Dictionary(Of String, String) From {
            {"email", email}
        })
            Using response As HttpResponseMessage = Http _
                .PostAsync($"{baseUrl}/api/register", content) _
                .GetAwaiter() _
                .GetResult()

                Return parse(response)
            End Using
        End Using
    End Function

    ''' <summary>
    ''' upload a nupkg file with the given email and TOTP code through the
    ''' custom multipart endpoint.
    ''' </summary>
    Public Function Upload(email As String, code As String, nupkgPath As String) As ApiResult
        Using form As New MultipartFormDataContent()
            Call form.Add(New StringContent(email), "email")
            Call form.Add(New StringContent(code), "code")

            Dim bytes As Byte() = File.ReadAllBytes(nupkgPath)
            Dim fileContent As New ByteArrayContent(bytes)
            fileContent.Headers.ContentType = New MediaTypeHeaderValue("application/octet-stream")
            Call form.Add(fileContent, "file", Path.GetFileName(nupkgPath))

            Using response As HttpResponseMessage = Http _
                .PostAsync($"{baseUrl}/api/v2/package", form) _
                .GetAwaiter() _
                .GetResult()

                Return parse(response)
            End Using
        End Using
    End Function

    Private Shared Function parse(response As HttpResponseMessage) As ApiResult
        Dim text As String = response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        Dim result As ApiResult = Nothing

        Try
            result = JsonSerializer.Deserialize(Of ApiResult)(text, JsonOptions)
        Catch
            result = Nothing
        End Try

        If result Is Nothing Then
            result = New ApiResult With {
                .ok = False,
                .message = text
            }
        End If

        If Not response.IsSuccessStatusCode Then
            result.ok = False
            If String.IsNullOrEmpty(result.message) Then
                result.message = $"{CInt(response.StatusCode)} {response.ReasonPhrase}"
            End If
        End If

        Return result
    End Function

    Private Shared Function normalize(server As String) As String
        Dim url As String = If(server, "").Trim().TrimEnd("/"c)

        If url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) OrElse
           url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) Then
            Return url
        End If

        Return "http://" & url
    End Function
End Class
