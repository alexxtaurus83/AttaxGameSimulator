@echo off
rem Generation 0 data, the recipe the overnight pipeline uses (see overnight-pipeline.cmd for the reasons, all measured):
rem   * the mover is the cheap engine (depth 3, training mode); it only has to produce realistic positions
rem   * the teacher is the SHIPPED engine (depth 3, normal mode + quiescence): policy label AND search score, 16 positions per game
rem   * blocked cells 0..6 as in the game
rem   * ~7,400 games/h on 12 cores, so 20000 games is ~2.7 h. Use a NEW --seed per run: the same seed+generation recreates the same games.
rem   * no nodes= on purpose: a node budget below the cost of depth 3 makes most moves fall back to depth 1 (Readme "Node budget")
if not exist data mkdir data
Attax.Console.exe selfplay --games 20000 --seed 1100 --generation 0 --blocked 0,6 --out data\gen0.bin ^
  --red classic:depth=3,train=true --teacher classic:depth=3,train=false ^
  --samples 16 --symmetry none ^
  --epsilonStart 0.25 --epsilonMid 0.10 --epsilonLate 0.02 --epsilonPly1 10 --epsilonPly2 25
Attax.Console.exe validate-log --path data\gen0.bin
