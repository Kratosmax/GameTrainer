@echo off
chcp 65001 >nul
title Hospital Rhapsody 修改器
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0HospTrainer.ps1"
pause