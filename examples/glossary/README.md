# An example glossary

`keyterms-islamic-sciences.txt` is a glossary of the Islamic sciences in IJMES transliteration: one term per line, `#`
for a comment. ClassScribe ships no glossary and sends none until your settings name one; this file is an example,
never built or published.

Its master copy is Gulzar's own, in his settings folder (`%APPDATA%\ClassScribe\keyterms-islamic-sciences.txt`), where he
edits it; this copy is refreshed from his only when he asks. The tests read it to check that every term in it is one
ElevenLabs takes.

To use it, or a glossary of your own, copy it into your settings folder and name it:

```powershell
Copy-Item examples\glossary\keyterms-islamic-sciences.txt "$env:APPDATA\ClassScribe\"
ClassScribe settings set ElevenLabs:KeytermsFile "%APPDATA%\ClassScribe\keyterms-islamic-sciences.txt"
```

A saved class can have a glossary of its own: `ClassScribe class set "Mantiq" --keyterms "%APPDATA%\ClassScribe\mantiq.txt"`.
The README's section on the glossary says the rest.
