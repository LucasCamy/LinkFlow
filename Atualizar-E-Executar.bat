@echo off
echo ===================================================
echo   LinkFlow - Atualizando e Iniciando Nova Versao
echo ===================================================
echo.
echo 1. Encerrando instancias abertas do LinkFlow...
taskkill /F /IM LinkFlow.App.exe 2>nul
timeout /t 1 /nobreak >nul

echo 2. Atualizando binarios em D:\Projetos\LinkFlow\bin\LinkFlow...
xcopy /Y /E /I "D:\Projetos\LinkFlow\bin\LinkFlow_v2\*" "D:\Projetos\LinkFlow\bin\LinkFlow\" >nul

echo 3. Iniciando LinkFlow como Administrador...
powershell -NoProfile -Command "Start-Process 'D:\Projetos\LinkFlow\bin\LinkFlow\LinkFlow.App.exe' -Verb RunAs"

echo.
echo Concluido! LinkFlow atualizado e rodando com sincronizacao completa!
pause
