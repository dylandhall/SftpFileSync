# SftpFileSync
A simple console app in .Net core to watch and sync files to a SFTP server.

This is a small personal project - it has not been written to be bulletproof, however it works quite well.

The use case this solves is developing on a windows computer, but hosting a webpack server (or similar) on a linux computer for live development.

It only supports login via password currently. It supports matching a root folder on client and host, and allows you to specify only certain subfolders to watch and update.
