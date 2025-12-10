Option Explicit On
Option Strict On

Imports System
Imports System.Collections.Generic
Imports System.Data
Imports System.IO
Imports System.Linq
Imports System.Diagnostics
Imports Autodesk.Revit.DB
Imports Autodesk.Revit.UI
Imports NPOI.HSSF.UserModel
Imports NPOI.SS.UserModel
Imports NPOI.XSSF.UserModel
Imports System.Windows.Forms ' WinForms 다이얼로그 사용

Namespace UI.Hub
    ' 커넥터 진단 (fix2 이벤트명/스키마 유지)
    Partial Public Class UiBridgeExternalEvent

        ' 최근 로드/실행 결과(엑셀 저장 시 기본 소스)
        Private lastConnRows As List(Of Dictionary(Of String, Object)) = Nothing

        ' 전체 커넥터 결과(엑셀 저장용)
        Private _connectorAllRows As List(Of Dictionary(Of String, Object)) = Nothing

        ' 디버그 로그를 웹(F12 콘솔)로 보내는 헬퍼
        Private Sub LogDebug(message As String)
            Try
                Dim ts As String = Date.Now.ToString("HH:mm:ss")
                SendToWeb("host:log", New With {
                    .message = $"[{ts}] {message}"
                })
            Catch
                ' 로깅 중 예외는 무시
            End Try
        End Sub

        ' 오류 로그용
        Private Sub LogError(message As String)
            Try
                Dim ts As String = Date.Now.ToString("HH:mm:ss")
                SendToWeb("host:error", New With {
                    .message = $"[{ts}] {message}"
                })
            Catch
            End Try
        End Sub

        Private Function SafePayloadSnapshot(payload As Object) As String
            If payload Is Nothing Then Return "(null)"
            Try
                Dim dict = TryCast(payload, IDictionary(Of String, Object))
                If dict IsNot Nothing Then
                    Dim parts As New List(Of String)()
                    For Each kv In dict
                        Dim v As Object = kv.Value
                        Dim text As String = If(v Is Nothing, "(null)", v.ToString())
                        parts.Add(kv.Key & "=" & text)
                    Next
                    Return "{" & String.Join(", ", parts) & "}"
                End If
                Return payload.ToString()
            Catch
                Return "(payload)"
            End Try
        End Function

#Region "핸들러 (Core에서 리플렉션으로 호출)"

        ' === connector:run ===
        Private Sub HandleConnectorRun(app As UIApplication, payload As Object)
            Try
                LogDebug("[connector] HandleConnectorRun 진입")
                LogDebug("[connector] payload 수신: " & SafePayloadSnapshot(payload))

                Dim uidoc = app.ActiveUIDocument
                Dim doc = If(uidoc Is Nothing, Nothing, uidoc.Document)
                If doc Is Nothing Then
                    LogError("[connector] 활성 문서가 없습니다.")
                    SendToWeb("revit:error", New With {.message = "활성 문서가 없습니다."})
                    SendToWeb("connector:done", New With {.ok = False, .message = "활성 문서가 없습니다."})
                    Return
                End If

                _connectorAllRows = Nothing

                ' === payload 파싱 ===
                Dim tol As Double = 1.0 ' 기본 1 inch
                Dim unit As String = "inch"
                Dim param As String = "Comments"
                Try
                    Dim vTol = GetProp(payload, "tol")
                    If vTol IsNot Nothing Then tol = Convert.ToDouble(vTol)
                Catch : End Try
                Try
                    Dim vUnit = TryCast(GetProp(payload, "unit"), String)
                    If Not String.IsNullOrEmpty(vUnit) Then unit = vUnit
                Catch : End Try
                Try
                    Dim vParam = TryCast(GetProp(payload, "param"), String)
                    If Not String.IsNullOrEmpty(vParam) Then param = vParam
                Catch : End Try
                LogDebug($"[connector] 파라미터 파싱 완료 (tol={tol}, unit={unit}, param={param})")

                ' === 단위 변환 → feet ===
                Dim tolFt As Double = 0.0
                Dim u = (If(unit, "inch")).Trim().ToLowerInvariant()
                If u = "mm" OrElse u = "millimeter" OrElse u = "millimeters" Then
                    tolFt = tol / 304.8R
                Else
                    ' inch 또는 기타 → inch 가정
                    tolFt = tol / 12.0R
                End If
                If tolFt < 0.0000001 Then tolFt = 0.0000001R
                LogDebug($"[connector] tolFt 계산 완료: {tolFt}")

                ' === 서비스 호출 ===
                LogDebug("[connector] 커넥터 수집/진단 실행 시작")
                Const PREVIEW_LIMIT As Integer = 150
                Dim rows As List(Of Dictionary(Of String, Object)) = Nothing
                Try
                    rows = Services.ConnectorDiagnosticsService.Run(app, tolFt, param)
                Catch ex As Exception
                    ' 네임스페이스 변동 대비 리플렉션 재시도
                    Try
                        Dim t = Type.GetType("KKY_Tool_Revit.Services.ConnectorDiagnosticsService, KKY_Tool_Revit")
                        If t Is Nothing Then t = Type.GetType("ConnectorDiagnosticsService")
                        If t IsNot Nothing Then
                            Dim m = t.GetMethod("Run", Reflection.BindingFlags.Public Or Reflection.BindingFlags.Static)
                            If m IsNot Nothing Then
                                rows = CType(m.Invoke(Nothing, New Object() {app, tolFt, param}), List(Of Dictionary(Of String, Object)))
                            End If
                        End If
                    Catch
                    End Try
                End Try
                If rows Is Nothing Then rows = New List(Of Dictionary(Of String, Object))()

                Try
                    Dim svcLog = Services.ConnectorDiagnosticsService.LastDebug
                    If svcLog IsNot Nothing Then
                        For Each line In svcLog
                            LogDebug("[connector][svc] " & line)
                        Next
                    End If
                Catch
                End Try

                Dim filteredRows = rows.Where(Function(r) ShouldIncludeRow(r)).ToList()

                Dim mismatchAll = filteredRows.Where(Function(r) IsMismatchRow(r)).ToList()
                Dim nearAll = filteredRows.Where(Function(r) IsNearConnection(r)).ToList()

                Dim mismatchPreview As List(Of Dictionary(Of String, Object)) = mismatchAll.Take(PREVIEW_LIMIT).ToList()
                Dim nearPreview As List(Of Dictionary(Of String, Object)) = nearAll.Take(PREVIEW_LIMIT).ToList()
                Dim previewRows As List(Of Dictionary(Of String, Object)) = filteredRows.Take(PREVIEW_LIMIT).ToList()

                _connectorAllRows = filteredRows
                lastConnRows = filteredRows

                Dim mismatchCount As Integer = mismatchAll.Count
                Dim okCount As Integer = Math.Max(filteredRows.Count - mismatchCount, 0)
                LogDebug($"[connector] 규칙/비교 로직 적용 완료: 정상 {okCount}개, 경고/오류 {mismatchCount}개")
                LogDebug($"[connector] 커넥터 수집 완료: 결과 행 {filteredRows.Count}개 (Mismatch={mismatchAll.Count}, Near={nearAll.Count})")

                LogDebug("[connector] 결과 전송 준비 완료, connector:done/connector:loaded emit 직전")
                Dim hasMore As Boolean = filteredRows.Count > PREVIEW_LIMIT
                SendToWeb("connector:loaded", New With {
                    .rows = previewRows,
                    .total = filteredRows.Count,
                    .previewCount = previewRows.Count,
                    .hasMore = hasMore,
                    .mismatch = New With {
                        .rows = mismatchPreview,
                        .total = mismatchAll.Count,
                        .previewCount = mismatchPreview.Count,
                        .hasMore = mismatchAll.Count > PREVIEW_LIMIT
                    },
                    .near = New With {
                        .rows = nearPreview,
                        .total = nearAll.Count,
                        .previewCount = nearPreview.Count,
                        .hasMore = nearAll.Count > PREVIEW_LIMIT
                    }
                })
                SendToWeb("connector:done", New With {
                    .rows = previewRows,
                    .total = filteredRows.Count,
                    .previewCount = previewRows.Count,
                    .hasMore = hasMore,
                    .mismatch = New With {
                        .rows = mismatchPreview,
                        .total = mismatchAll.Count,
                        .previewCount = mismatchPreview.Count,
                        .hasMore = mismatchAll.Count > PREVIEW_LIMIT
                    },
                    .near = New With {
                        .rows = nearPreview,
                        .total = nearAll.Count,
                        .previewCount = nearPreview.Count,
                        .hasMore = nearAll.Count > PREVIEW_LIMIT
                    }
                })
                LogDebug("[connector] 결과 전송 완료, connector:done emit")
                LogDebug("[connector] HandleConnectorRun 정상 종료")

            Catch ex As Exception
                LogError("[connector] 검사 중 예외 발생: " & ex.ToString())
                SendToWeb("connector:done", New With {.ok = False, .message = ex.Message})
                SendToWeb("revit:error", New With {.message = "실행 실패: " & ex.Message})
            End Try
        End Sub

        ' === connector:save-excel ===
        Private Sub HandleConnectorSaveExcel(app As UIApplication, payload As Object)
            Try
                Dim rows As List(Of Dictionary(Of String, Object)) = _connectorAllRows
                If rows Is Nothing OrElse rows.Count = 0 Then rows = TryGetRowsFromPayload(payload)
                If rows Is Nothing OrElse rows.Count = 0 Then rows = lastConnRows

                If rows Is Nothing OrElse rows.Count = 0 Then
                    SendToWeb("revit:error", New With {.message = "저장할 데이터가 없습니다."})
                    Return
                End If

                Dim mismatchCount As Integer = CountMismatches(rows)
                Dim saved As String = SaveRowsToExcel(rows, mismatchCount)

                SendToWeb("connector:saved", New With {.path = saved})

            Catch ex As Exception
                SendToWeb("revit:error", New With {.message = "엑셀 저장 실패: " & ex.Message})
            End Try
        End Sub

#End Region

#Region "엑셀 입출력/유틸 (스키마 불변)"

        Private Function TryReadExcelAsDataTable() As DataTable
            Using ofd As New OpenFileDialog()
                ofd.Filter = "Excel Files|*.xlsx;*.xls"
                ofd.Multiselect = False
                If ofd.ShowDialog() <> DialogResult.OK Then Return Nothing

                Dim filePath = ofd.FileName
                Using fs As New FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
                    Dim wb As IWorkbook
                    Dim ext As String = System.IO.Path.GetExtension(filePath)
                    If ext IsNot Nothing AndAlso ext.Equals(".xls", StringComparison.OrdinalIgnoreCase) Then
                        wb = New HSSFWorkbook(fs)
                    Else
                        wb = New XSSFWorkbook(fs)
                    End If

                    Dim sh = wb.GetSheetAt(0)
                    Dim dt As New DataTable()

                    ' 헤더
                    Dim hr = sh.GetRow(sh.FirstRowNum)
                    If hr Is Nothing Then Return Nothing
                    For c = 0 To hr.LastCellNum - 1
                        Dim name = If(hr.GetCell(c)?.ToString(), $"C{c + 1}")
                        dt.Columns.Add(name)
                    Next

                    ' 데이터
                    For r = sh.FirstRowNum + 1 To sh.LastRowNum
                        Dim sr = sh.GetRow(r)
                        If sr Is Nothing Then Continue For
                        Dim dr = dt.NewRow()
                        For c = 0 To dt.Columns.Count - 1
                            dr(c) = If(sr.GetCell(c)?.ToString(), "")
                        Next
                        dt.Rows.Add(dr)
                    Next

                    Return dt
                End Using
            End Using
        End Function

        Private Function DataTableRows(dt As DataTable) As List(Of Dictionary(Of String, Object))
            Dim list As New List(Of Dictionary(Of String, Object))()
            For Each r As DataRow In dt.Rows
                Dim d As New Dictionary(Of String, Object)(StringComparer.OrdinalIgnoreCase)
                For Each c As DataColumn In dt.Columns
                    d(c.ColumnName) = If(r(c), "")
                Next
                list.Add(d)
            Next
            Return list
        End Function

        Private Function TryGetRowsFromPayload(payload As Object) As List(Of Dictionary(Of String, Object))
            If payload Is Nothing Then Return Nothing
            Try
                Dim d = TryCast(payload, IDictionary(Of String, Object))
                If d IsNot Nothing AndAlso d.ContainsKey("rows") Then
                    Return TryCast(d("rows"), List(Of Dictionary(Of String, Object)))
                End If
            Catch
            End Try
            Return Nothing
        End Function

        Private Shared Function ReadField(r As Dictionary(Of String, Object), key As String) As String
            If r Is Nothing Then Return String.Empty
            If r.ContainsKey(key) AndAlso r(key) IsNot Nothing Then
                Return r(key).ToString()
            End If
            Return String.Empty
        End Function

        Private Shared Function IsNearConnection(r As Dictionary(Of String, Object)) As Boolean
            Dim conn As String = ReadField(r, "ConnectionType")
            If String.IsNullOrEmpty(conn) Then conn = ReadField(r, "Connection Type")
            Return String.Equals(conn, "Near", StringComparison.OrdinalIgnoreCase)
        End Function

        Private Shared Function IsMismatchRow(r As Dictionary(Of String, Object)) As Boolean
            Dim status As String = ReadField(r, "Status")
            Return String.Equals(status, "Mismatch", StringComparison.OrdinalIgnoreCase)
        End Function

        Private Shared Function ShouldIncludeRow(r As Dictionary(Of String, Object)) As Boolean
            ' Near 최우선 포함, Mismatch 전체 포함, Connected + OK 는 제외
            If IsNearConnection(r) Then Return True
            If IsMismatchRow(r) Then Return True

            Dim status As String = ReadField(r, "Status")
            Dim conn As String = ReadField(r, "ConnectionType")
            If String.IsNullOrEmpty(conn) Then conn = ReadField(r, "Connection Type")

            Dim isOk As Boolean = String.Equals(status, "OK", StringComparison.OrdinalIgnoreCase)
            Dim isConnected As Boolean = String.Equals(conn, "Connected", StringComparison.OrdinalIgnoreCase)

            If isConnected AndAlso isOk Then Return False

            Return False
        End Function

        Private Function CountMismatches(rows As List(Of Dictionary(Of String, Object))) As Integer
            If rows Is Nothing Then Return 0
            Dim cnt As Integer = 0
            For Each row In rows
                Dim status As String = Nothing
                If row IsNot Nothing AndAlso row.ContainsKey("Status") AndAlso row("Status") IsNot Nothing Then
                    status = row("Status").ToString()
                End If
                If Not String.Equals(status, "OK", StringComparison.OrdinalIgnoreCase) Then
                    cnt += 1
                End If
            Next
            Return cnt
        End Function

        Private Function SaveRowsToExcel(rows As List(Of Dictionary(Of String, Object)), Optional mismatchCount As Integer = -1) As String
            Dim todayToken As String = Date.Now.ToString("yyMMdd")
            Dim count As Integer = If(mismatchCount < 0, CountMismatches(rows), mismatchCount)
            Dim defaultName As String = $"{todayToken}_커넥터기반 속성값 검토 결과_{count}개.xlsx"

            Using sfd As New SaveFileDialog()
                sfd.Filter = "Excel Workbook (*.xlsx)|*.xlsx"
                sfd.FileName = defaultName
                If sfd.ShowDialog() <> DialogResult.OK Then Throw New OperationCanceledException()

                Dim savePath = sfd.FileName
                Using fs As New FileStream(savePath, FileMode.Create, FileAccess.Write, FileShare.None)
                    Using wb As New XSSFWorkbook()
                        Dim sh = wb.CreateSheet("Connectors")
                        If rows Is Nothing OrElse rows.Count = 0 Then
                            wb.Write(fs) : Return savePath
                        End If

                        ' 헤더: 첫 행의 키 순서 고정
                        Dim headers = rows(0).Keys.ToList()
                        Dim statusIdx As Integer = headers.FindIndex(Function(h) String.Equals(h, "Status", StringComparison.OrdinalIgnoreCase))
                        Dim connIdx As Integer = headers.FindIndex(Function(h) String.Equals(h, "ConnectionType", StringComparison.OrdinalIgnoreCase) _
                                                                      OrElse String.Equals(h, "Connection Type", StringComparison.OrdinalIgnoreCase))

                        ' 통일된 테두리/헤더/행 색상 스타일 구축
                        Dim baseStyle As ICellStyle = CreateBorderedStyle(wb)
                        Dim headerStyle As ICellStyle = CreateHeaderStyle(wb, baseStyle)
                        Dim mismatchStyle As ICellStyle = CreateFillStyle(wb, baseStyle, New Byte() {&HF9, &HD3, &HD7}) ' light red
                        Dim matchStyle As ICellStyle = CreateFillStyle(wb, baseStyle, New Byte() {&HD6, &HEF, &HD6})   ' light green
                        Dim nearStyle As ICellStyle = CreateFillStyle(wb, baseStyle, New Byte() {&HFA, &HF3, &HD1})    ' light yellow

                        Dim headerRow = sh.CreateRow(0)
                        For i = 0 To headers.Count - 1
                            Dim c = headerRow.CreateCell(i)
                            c.SetCellValue(headers(i))
                            c.CellStyle = headerStyle
                        Next

                        ' 데이터
                        Dim r As Integer = 1
                        For Each row In rows
                            Dim sr = sh.CreateRow(r) : r += 1

                            Dim statusVal As String = If(statusIdx >= 0, SafeCellString(row, headers(statusIdx)), "")
                            Dim connVal As String = If(connIdx >= 0, SafeCellString(row, headers(connIdx)), "")
                            Dim styleToUse As ICellStyle = baseStyle

                            Dim statusNorm = statusVal.Trim().ToLowerInvariant()
                            If String.Equals(statusNorm, "mismatch", StringComparison.OrdinalIgnoreCase) Then
                                styleToUse = mismatchStyle
                            ElseIf statusNorm = "ok" OrElse statusNorm = "match" OrElse statusNorm = "matched" Then
                                styleToUse = matchStyle
                            ElseIf String.Equals(connVal.Trim(), "Near", StringComparison.OrdinalIgnoreCase) Then
                                styleToUse = nearStyle
                            End If

                            For c = 0 To headers.Count - 1
                                Dim key = headers(c)
                                Dim v = If(row.ContainsKey(key), row(key), Nothing)
                                Dim cell = sr.CreateCell(c)
                                cell.SetCellValue(If(v Is Nothing, "", v.ToString()))
                                cell.CellStyle = styleToUse
                            Next
                        Next

                        ' 자동 너비
                        For c = 0 To headers.Count - 1
                            Try : sh.AutoSizeColumn(c) : Catch : End Try
                        Next

                        wb.Write(fs)
                    End Using
                End Using

                Return savePath
            End Using
        End Function

        ' 테두리/헤더/색상 스타일 헬퍼 (같은 워크북 내 공유)
        Private Shared Function CreateBorderedStyle(wb As XSSFWorkbook) As ICellStyle
            Dim st As ICellStyle = wb.CreateCellStyle()
            st.BorderTop = NPOI.SS.UserModel.BorderStyle.Thin
            st.BorderBottom = NPOI.SS.UserModel.BorderStyle.Thin
            st.BorderLeft = NPOI.SS.UserModel.BorderStyle.Thin
            st.BorderRight = NPOI.SS.UserModel.BorderStyle.Thin
            Return st
        End Function

        Private Shared Function CreateHeaderStyle(wb As XSSFWorkbook, baseStyle As ICellStyle) As ICellStyle
            Dim st As XSSFCellStyle = CType(wb.CreateCellStyle(), XSSFCellStyle)
            st.CloneStyleFrom(baseStyle)
            st.FillPattern = NPOI.SS.UserModel.FillPattern.SolidForeground
            ' use available ctor for current NPOI version
            st.SetFillForegroundColor(New XSSFColor(New Byte() {&H2A, &H3B, &H52}))

            Dim f As XSSFFont = CType(wb.CreateFont(), XSSFFont)
            f.IsBold = True
            f.Color = IndexedColors.White.Index
            st.SetFont(f)
            Return st
        End Function

        Private Shared Function CreateFillStyle(wb As XSSFWorkbook, baseStyle As ICellStyle, rgb As Byte()) As ICellStyle
            Dim st As XSSFCellStyle = CType(wb.CreateCellStyle(), XSSFCellStyle)
            st.CloneStyleFrom(baseStyle)
            st.FillPattern = NPOI.SS.UserModel.FillPattern.SolidForeground
            ' same ctor form as header style to keep compatibility with the current NPOI version
            st.SetFillForegroundColor(New XSSFColor(rgb))
            Return st
        End Function

        Private Shared Function SafeCellString(row As Dictionary(Of String, Object), key As String) As String
            If row Is Nothing OrElse String.IsNullOrEmpty(key) OrElse Not row.ContainsKey(key) Then Return String.Empty
            Dim v = row(key)
            Return If(v Is Nothing, String.Empty, v.ToString())
        End Function

#End Region

    End Class
End Namespace
