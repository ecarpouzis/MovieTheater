$exe  = "F:/Work/MovieTheater/src/MovieTheater.BooksHost/bin/Debug/net10.0/MovieTheater.BooksHost.exe"
$db   = "F:/Work/MovieTheater/data/books/v2/books.db"
$legs = "F:/Work/MovieTheater/data/books/v2/books-legs.db"
Set-Location "F:/Work/MovieTheater"
"=== collected-editions ==="
& $exe books-collected-editions --db $db --legs $legs | Select-Object -Last 2
"=== reading-order ==="
& $exe books-reading-order --db $db | Select-Object -Last 2
"=== containment ==="
& $exe books-containment --db $db | Select-Object -Last 2
"=== DONE ==="
