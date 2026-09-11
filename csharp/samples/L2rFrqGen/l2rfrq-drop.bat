@echo off
rem UTAU 音源フォルダ（または .wav）をこのファイルにドラッグ＆ドロップすると
rem 周波数表 *.frq.l2r をまとめて生成します。
setlocal

if "%~1"=="" (
  echo.
  echo   UTAU 音源フォルダ、または .wav ファイルを
  echo   このバッチファイルにドラッグ ^& ドロップしてください。
  echo.
  echo   複数まとめてドロップできます。すでに最新の表があるファイルは
  echo   自動的にスキップされます。
  echo.
  pause
  exit /b 1
)

"%~dp0l2rfrq.exe" %*

echo.
pause
