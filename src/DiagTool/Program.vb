Imports System.IO.Compression
Imports Nuget

Module Program

    Function Main(args As String()) As Integer
        Dim path As String = args(0)

        Using zip As ZipArchive = ZipFile.OpenRead(path)
            For Each entry As ZipArchiveEntry In zip.Entries
                Dim name As String = entry.FullName.Replace("\"c, "/"c)

                If Not name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) Then
                    Continue For
                End If

                Using s = entry.Open()
                    Dim buffer(16383) As Byte
                    Dim total As Integer = 0
                    Dim n As Integer

                    Do While total < buffer.Length
                        n = s.Read(buffer, total, buffer.Length - total)
                        If n <= 0 Then Exit Do
                        total += n
                    Loop

                    ' step by step replica of the pe walk
                    Dim mz As Integer = readU16(buffer, 0)
                    Dim peOffset As Integer = readI32(buffer, &H3C)
                    Dim sig As Integer = readI32(buffer, peOffset)
                    Dim magic As Integer = readU16(buffer, peOffset + 24)
                    Dim dd As Integer = If(magic = &H20B, peOffset + 24 + 112, peOffset + 24 + 96)
                    Dim clr As Integer = readI32(buffer, dd + 14 * 8)

                    Console.WriteLine($"entry={name} total={total}")
                    Console.WriteLine($"  mz=0x{mz:X4} pe={peOffset} sig=0x{sig:X8} magic=0x{magic:X4} dd={dd} clr=0x{clr:X}")

                    ' byte level check of the four signature spots
                    Console.WriteLine($"  peBytes={buffer(peOffset):X2}-{buffer(peOffset + 1):X2}-{buffer(peOffset + 2):X2}-{buffer(peOffset + 3):X2}")
                    Console.WriteLine($"  magicBytes={buffer(peOffset + 24):X2}-{buffer(peOffset + 25):X2}")
                    Console.WriteLine($"  clrBytes={buffer(dd + 112):X2}-{buffer(dd + 113):X2}-{buffer(dd + 114):X2}-{buffer(dd + 115):X2}")

                    Dim bsjb As Boolean = False
                    For i As Integer = 0 To total - 4
                        If buffer(i) = &H42 AndAlso buffer(i + 1) = &H53 AndAlso buffer(i + 2) = &H4A AndAlso buffer(i + 3) = &H42 Then
                            bsjb = True
                            Console.WriteLine($"  BSJB at {i}")
                            Exit For
                        End If
                    Next
                    Console.WriteLine($"  bsjb={bsjb}")

                    Using s2 = entry.Open()
                        Console.WriteLine($"  library IsManagedAssembly={PackageValidator.IsManagedAssembly(s2)}")
                    End Using
                End Using
            Next
        End Using

        Return 0
    End Function

    Private Function readU16(buffer As Byte(), offset As Integer) As Integer
        Return buffer(offset) Or (buffer(offset + 1) << 8)
    End Function

    Private Function readI32(buffer As Byte(), offset As Integer) As Integer
        Return buffer(offset) Or (buffer(offset + 1) << 8) Or (buffer(offset + 2) << 16) Or (buffer(offset + 3) << 24)
    End Function
End Module
