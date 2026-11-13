@echo off
rem Launch congee without a console window. Pass -c other.conf to use another config.
start "" "%~dp0.venv\Scripts\pythonw.exe" "%~dp0congee.py" %*
