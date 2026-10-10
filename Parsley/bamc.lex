Keyword 
number \-?(0|[1-9][0-9]*)(\.[0-9]+)?([Ee][\+\-]?[0-9]+)?
string "([^\n"\\]|\\([btrnf"\\/]|(u[0-9A-Fa-f]{4})))*"
lbracket \[
rbracket \]
lbrace \{
rbrace \}
colon :
comma ,
.whitespace [\n\r\t ]+
.block_comment \/\*(\n|.)*?\*\/
.line_comment //.*$
EOL \n
BROWSER_ARG 
FEATURE_ARG 
