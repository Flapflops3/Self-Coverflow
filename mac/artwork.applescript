on run argv
    set dbid to (item 1 of argv) as integer
    set outputFile to POSIX file (item 2 of argv)
    tell application id "com.apple.Music"
        set theTrack to first track of library playlist 1 whose database ID is dbid
        if (count artworks of theTrack) is 0 then return
        set pictureBytes to raw data of artwork 1 of theTrack
    end tell
    set fd to open for access outputFile with write permission
    try
        set eof fd to 0
        write pictureBytes to fd
        close access fd
    on error messageText number messageNumber
        close access fd
        error messageText number messageNumber
    end try
end run

