@echo off
rem Double-click to start the guided first car test.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0FirstCarTest.ps1" %*
