@echo off
rem Train one generation (the settings the overnight pipeline uses). --out must be a new/empty folder (or add --resume / --overwrite).
rem Pass every log for this generation; never pass a log together with its own relabeled copy (the loader rejects that).
rem Value head: learns the teacher's search score (--value-target score), needs logs written by the current selfplay/relabel.
rem For older logs without scores use --value-target outcome (noisy; weight 0.3 kept the policy better in the first run).
if not exist models mkdir models
python train.py --data data\gen0.bin --out models\gen0 --device cuda --amp --channels 128 --layers 8 --batch-size 1024 --epochs 60 --lr 1e-3 --lr-schedule cosine --value-target score --score-scale 4e6 --value-weight 1.0 --val-fraction 0.05 --keep-best --gpu-log 10
