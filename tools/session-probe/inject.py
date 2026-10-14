"""Types text into another terminal: inject.py /dev/pts/N 'text' (newline appended)."""
import fcntl, os, sys, termios
fd = os.open(sys.argv[1], os.O_RDWR)
for ch in (sys.argv[2] + "\n").encode():
    fcntl.ioctl(fd, termios.TIOCSTI, bytes([ch]))
