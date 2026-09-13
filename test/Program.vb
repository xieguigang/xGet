Imports System.Text
Imports Nuget

Module Program
    Sub Main(args As String())
        ' 子命令分派：阅读器 api 文档生成器的测试
        If args IsNot Nothing AndAlso args.Length > 0 Then
            Dim command As String = args(0).TrimStart("-"c, "/"c).ToLowerInvariant()

            Select Case command
                Case "apidoc"
                    Environment.ExitCode = ApiDocTest.Run(args.Skip(1).ToArray)
                    Return
            End Select
        End If

        ' 0. 自检：确认实现与 RFC 6238 官方测试向量一致
        Console.WriteLine("RFC 6238 自检: " & If(RunSelfTest(), "通过", "失败"))
        Console.WriteLine()

        ' 1.【服务端】为用户生成密钥（实际项目中应加密后存入数据库）
        Dim secretBase32 As String = TotpModule.GenerateSecretKeyBase32()
        Console.WriteLine("Base32 密钥: " & secretBase32)

        ' 2.【服务端】生成 otpauth:// URI（用二维码库转成图片，供验证器 App 扫描）
        Console.WriteLine("otpauth URI: " & TotpModule.BuildOtpAuthUri(secretBase32, "user@example.com", "MySite"))
        Console.WriteLine()

        ' 3. 模拟验证器 App 显示的当前 6 位验证码
        Console.WriteLine("当前验证码: " & TotpModule.GenerateTotp(secretBase32) &
                          "（剩余 " & TotpModule.GetRemainingSeconds() & " 秒有效）")

        ' 4.【服务端】校验用户登录时输入的验证码
        Console.Write("请输入上面的验证码: ")
        Dim input As String = Console.ReadLine()
        Console.WriteLine("校验结果: " & If(TotpModule.VerifyTotp(secretBase32, input), "验证通过", "验证失败"))

        If Not Console.IsInputRedirected Then
            Console.ReadKey()
        End If
    End Sub

    '==================== 6. RFC 6238 官方测试向量自检 ====================

    ''' <summary>
    ''' 用 RFC 6238 附录 B 的官方测试向量验证本实现。返回 True 表示算法实现正确。
    ''' </summary>
    Public Function RunSelfTest() As Boolean
        Dim secret As Byte() = Encoding.ASCII.GetBytes("12345678901234567890")
        Dim times() As Long = {59, 1111111109, 1111111111, 1234567890, 2000000000, 20000000000}
        Dim expected() As String = {"94287082", "07081804", "14050471", "89005924", "69279037", "65353130"}

        For i As Integer = 0 To times.Length - 1
            Dim actual As String = GenerateTotp(secret, times(i), 30, 8, TotpHmacAlgorithm.SHA1)
            If Not String.Equals(actual, expected(i), StringComparison.Ordinal) Then
                Return False
            End If
        Next
        Return True
    End Function
End Module
