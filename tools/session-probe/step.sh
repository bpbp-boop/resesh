#!/bin/sh
# step.sh <state|drop|unseen|attach|agent> : one in-app spike step, run on the host.
cd /root/.local/share/resesh-spike || exit 1
export ZMX_DIR=$PWD/sock
case "$1" in
  state)
    ./zmx list
    ps -t pts/0 -o pid,stat,args ;;
  drop)
    # The connection dies: the client on the app's terminal loses its tty.
    for p in $(ps -t pts/0 -o pid=,comm= | awk '$2=="zmx"{print $1}'); do kill -HUP "$p"; done
    sleep 1; ./zmx list ;;
  stopagent)
    for p in $(pgrep -f 'python3 .*/sho[w].py'); do kill "$p"; done
    sleep 1; ./zmx list ;;
  unseen)
    timeout 20 ./zmx run app echo made-while-detached >/dev/null 2>&1
    echo "run rc=$?"; ./zmx list ;;
  agent)
    (timeout 5 ./zmx run app python3 $PWD/show.py $2 2 >/dev/null 2>&1 &)
    sleep 4; ./zmx list ;;
  attach)
    python3 inject.py /dev/pts/0 " printf '\\033[2J\\033[3J\\033[H'; ZMX_DIR=$ZMX_DIR $PWD/zmx attach app"
    sleep 2; ./zmx list ;;
esac
