@echo off
REM Wrapper for publish.ps1 -- avoids the "running scripts is disabled" error
REM (default ExecutionPolicy on Windows client is Restricted).
REM
REM This file is intentionally pure ASCII: .cmd files with non-ASCII content
REM get mangled by the console codepage and can fail in confusing ways.
REM
REM Usage:
REM   publish.cmd
REM   publish.cmd -SkipTests
REM   publish.cmd -Configuration Debug
REM
REM IMPORTANT: run this in a normal Windows terminal, NOT inside an AI agent
REM shell. See HANDOVER.md section 5 for why the agent shell cannot build.

setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish.ps1" %*
exit /b %ERRORLEVEL%
