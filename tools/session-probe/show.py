"""A stand-in agent: prints styled output, reports status with plain OSC 7501, then waits."""
import sys, time
E = chr(27)
ST = E + "\\"
o = sys.stdout
for i in range(1, 61):
    o.write("history line %d\r\n" % i)
o.write(E + "[38;2;80;200;120mtruecolor green" + E + "[0m  " + E + "[4:3mcurly underline" + E + "[0m  "
        + E + "]8;;https://resesh.net" + ST + "an OSC 8 link" + E + "]8;;" + ST + "\r\n")
o.write(E + "]0;claude" + chr(7))
o.write(E + "]7501;state=" + sys.argv[1] + ":app=claude" + ST)
o.write("status sent: " + sys.argv[1] + " (plain OSC 7501, no wrapper)\r\n")
o.flush()
time.sleep(float(sys.argv[2]))
o.write("line printed %s seconds later\r\n" % sys.argv[2])
o.flush()
time.sleep(900)
