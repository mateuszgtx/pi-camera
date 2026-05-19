#!/bin/bash
# Ukrywa migający kursor tekstowy na konsoli Linux.
set +e
printf '\033[?25l' > /dev/tty1 2>/dev/null
printf '\033[?17;0;0c' > /dev/tty1 2>/dev/null
setterm -cursor off -blank 0 -powersave off -powerdown 0 < /dev/tty1 2>/dev/null
