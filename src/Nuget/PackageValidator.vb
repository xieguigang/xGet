Imports System.IO
Imports System.IO.Compression

''' <summary>
''' the content validator of the uploaded nuget packages: the feed only accepts
''' library packages which ship at least one managed .NET clr dll inside their
''' ``lib`` folder. A package which carries any executable image (``.exe``) or
''' which ships no managed clr assembly at all is rejected before any file is
''' written to its final location and before any database row is created.
''' 
''' the ``managed`` test reads the pe header of the candidate dll only: a dll
''' counts as a managed assembly when its optional header declares a non zero
''' clr runtime header data directory (data directory index 14). the check is
''' purely structural, no assembly is loaded and no code is executed.
''' </summary>
Public Module PackageValidator

    ''' <summary>
    ''' validate the ``lib`` content of one uploaded nupkg.
    ''' </summary>
    ''' <param name="nupkgPath">the physical nupkg path.</param>
    ''' <param name="rejectReason">the human readable reject reason (empty when accepted).</param>
    ''' <returns><c>True</c> when the package content is acceptable.</returns>
    Public Function Validate(nupkgPath As String, ByRef rejectReason As String) As Boolean
        rejectReason = ""

        If String.IsNullOrEmpty(nupkgPath) OrElse Not File.Exists(nupkgPath) Then
            rejectReason = "the package file was not found."
            Return False
        End If

        Using zip As ZipArchive = ZipFile.OpenRead(nupkgPath)
            Dim managedDll As Boolean = False

            For Each entry As ZipArchiveEntry In zip.Entries
                Dim name As String = entry.FullName.Replace("\"c, "/"c)

                If Not name.StartsWith("lib/", StringComparison.OrdinalIgnoreCase) Then
                    Continue For
                End If

                Dim extension As String = Path.GetExtension(name).ToLowerInvariant()

                ' rule 1: executable images are never accepted
                If extension = ".exe" Then
                    rejectReason = $"the package ships an executable image in its lib folder ('{name}'): " &
                                   "only library packages with managed .net clr dll assemblies are accepted."
                    Return False
                End If

                ' rule 2: at least one managed clr dll is required
                If extension = ".dll" AndAlso Not managedDll Then
                    Try
                        Using stream As Stream = entry.Open()
                            If IsManagedAssembly(stream) Then
                                managedDll = True
                            End If
                        End Using
                    Catch
                        ' a truncated or fake dll simply does not count
                    End Try
                End If
            Next

            If Not managedDll Then
                rejectReason = "the package ships no managed .net clr dll in its lib folder: " &
                               "only library packages with managed .net clr dll assemblies are accepted."
                Return False
            End If
        End Using

        Return True
    End Function

    ''' <summary>
    ''' structural test whether the given pe image is a managed assembly.
    ''' 
    ''' primary test: the clr runtime header entry (data directory index 14) of
    ''' the optional header must be non zero. fallback test: the presence of the
    ''' ``BSJB`` clr metadata root signature inside the image, which every
    ''' managed assembly carries (and which a plain native dll does not). the
    ''' fallback keeps the check working when the pe offsets of a hostile or
    ''' unusual image can not be followed; the check is purely structural, no
    ''' assembly is loaded and no code is executed.
    ''' 
    ''' the check reads at most one 16 KB buffer from the stream and never uses
    ''' ``Stream.Length``/``Stream.Seek``: the deflate stream of a zip entry
    ''' does not support them.
    ''' </summary>
    ''' <param name="stream">the pe image stream (read from the current position).</param>
    Public Function IsManagedAssembly(stream As Stream) As Boolean
        If stream Is Nothing OrElse Not stream.CanRead Then
            Return False
        End If

        Try
            Dim buffer(16383) As Byte
            Dim total As Integer = 0
            Dim read As Integer

            Do While total < buffer.Length
                read = stream.Read(buffer, total, buffer.Length - total)

                If read <= 0 Then
                    Exit Do
                End If

                total += read
            Loop

            ' the dos header needs at least 64 bytes
            If total < &H40 Then
                Return False
            End If

            ' dos header: "MZ" (little endian 0x4D 'M', 0x5A 'Z')
            If readU16(buffer, 0) <> &H5A4D Then
                Return False
            End If

            If hasClrDataDirectory(buffer, total) Then
                Return True
            End If

            ' fallback: the "BSJB" metadata root signature of a managed assembly
            Return hasMetadataSignature(buffer, total)
        Catch
            Return False
        End Try
    End Function

    ''' <summary>
    ''' the primary pe walk: dos e_lfanew -> "PE\0\0" -> optional header magic
    ''' -> data directory index 14 (the clr runtime header) must be non zero.
    ''' </summary>
    Private Function hasClrDataDirectory(buffer As Byte(), total As Integer) As Boolean
        Dim peOffset As Integer = readI32(buffer, &H3C)

        If peOffset <= 0 OrElse peOffset + 264 > total Then
            Return False
        End If

        ' the "PE\0\0" signature
        If readI32(buffer, peOffset) <> &H4550 Then
            Return False
        End If

        ' the optional header follows the 20 bytes coff header
        Dim optionalHeader As Integer = peOffset + 24
        Dim magic As Integer = readU16(buffer, optionalHeader)
        Dim dataDirectories As Integer

        Select Case magic
            Case &H10BI  ' pe32
                dataDirectories = optionalHeader + 96
            Case &H20BI  ' pe32+
                dataDirectories = optionalHeader + 112
            Case Else
                Return False
        End Select

        ' data directory index 14: the clr runtime header
        Dim clrRva As Integer = readI32(buffer, dataDirectories + 14 * 8)

        Return clrRva <> 0
    End Function

    ''' <summary>
    ''' search the image window for the "BSJB" signature of the clr metadata
    ''' root (".net metadata"). every managed assembly carries it, usually near
    ''' the end of the file.
    ''' </summary>
    Private Function hasMetadataSignature(buffer As Byte(), total As Integer) As Boolean
        For i As Integer = 0 To total - 4
            If buffer(i) = &H42 AndAlso buffer(i + 1) = &H53 AndAlso
               buffer(i + 2) = &H4A AndAlso buffer(i + 3) = &H42 Then
                Return True
            End If
        Next

        Return False
    End Function

    ''' <summary>
    ''' little endian scalar readers. the byte values must be widened with
    ''' ``CInt`` before shifting: in VB ``Byte &lt;&lt; 8`` yields a Byte again (the
    ''' shift count is taken modulo 8), which silently drops the high bytes.
    ''' </summary>
    Private Function readU16(buffer As Byte(), offset As Integer) As Integer
        Return CInt(buffer(offset)) Or (CInt(buffer(offset + 1)) << 8)
    End Function

    Private Function readI32(buffer As Byte(), offset As Integer) As Integer
        Return CInt(buffer(offset)) Or (CInt(buffer(offset + 1)) << 8) Or
               (CInt(buffer(offset + 2)) << 16) Or (CInt(buffer(offset + 3)) << 24)
    End Function
End Module
