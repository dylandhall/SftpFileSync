# SftpFileSync
A simple console app in .Net core to watch and sync files to a SFTP server.

This is a small personal project - it has not been written to be bulletproof, however it works quite well.

You can download a current version binary under the current release in this repository, just extract to a folder and run for configuration options.

The use case this solves is developing on a windows computer, but hosting a webpack server (or similar) on a linux computer for live development.

It only supports login via password currently. It supports matching a root folder on client and host, and allows you to specify only certain subfolders to watch and update.

It watches updates but only actually updates a finalised list after 50ms of inactivity, so it avoids attempting to create and delete a number of temporary files often used by editors. I would suggest matching the root folders by doing a git clone of the repository on both the host and client.
