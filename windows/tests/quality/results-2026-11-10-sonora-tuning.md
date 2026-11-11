# Amanu quality run sonora-tuning

## Clean transcription (no echo, no noise)

| Set | Words | WER % | CER % |
|---|---|---|---|

WER = words wrong per 100 words. CER = characters wrong per 100 characters.

## Mixed calls, all conditions together

| Echo remover | My WER % | Echo words in my text | Their WER % | Echo removed (dB) | Echo time (s) |
|---|---|---|---|---|---|
| off | 58.5 | 603 | 8.1 | 0.0 | 0.0 |
| sonora | 38.9 | 4 | 8.1 | 17.5 | 39.1 |
| fast | 28.7 | 0 | 8.1 | 11.4 | 511.4 |
| thorough | 25.4 | 2 | 8.1 | 22.0 | 928.9 |
| sonora-linear | 44.1 | 124 | 8.1 | 2.7 | 40.2 |
| sonora-nearend | 38.9 | 3 | 8.1 | 11.5 | 39.4 |
| sonora-gentle | 39.5 | 3 | 8.1 | 11.3 | 39.5 |
| sonora-long | 37.2 | 5 | 8.1 | 14.0 | 48.6 |
| sonora-long80 | 37.9 | 2 | 8.1 | 9.7 | 60.0 |
| sonora-long80-nearend | 40.0 | 21 | 8.1 | 5.4 | 60.4 |

## Real device echo (Microsoft AEC Challenge, far end only)

| Echo remover | Echo removed (dB) | Time (s) |
|---|---|---|
| off | 0.0 | – |
| sonora | 21.7 | 4.9 |
| fast | 7.3 | 74.0 |
| thorough | 19.4 | 140.7 |
| sonora-linear | 4.7 | 5.1 |
| sonora-nearend | 15.1 | 5.3 |
| sonora-gentle | 15.0 | 5.3 |
| sonora-long | 16.4 | 6.1 |
| sonora-long80 | 10.8 | 8.4 |
| sonora-long80-nearend | 5.7 | 8.7 |

## My WER %

Rows: noise type and echo level (SER: lower = louder echo). Columns: noise level (SNR: lower = louder noise).

| Remover | Noise | SER | clean | +20 dB | +10 dB | +5 dB | +0 dB | -5 dB |
|---|---|---|---|---|---|---|---|---|
| off | office | +5 | 29.8 | 22.7 | 28.7 | 29.3 | 11.6 | 59.1 |
| off | office | -5 | 55.8 | 58.0 | 55.8 | 54.7 | 55.2 | 82.3 |
| off | office | -15 | 66.9 | 66.9 | 66.9 | 64.6 | 71.3 | 87.8 |
| off | cafe | +5 | 15.8 | 18.1 | 18.1 | 26.0 | 84.7 | 98.3 |
| off | cafe | -5 | 45.8 | 49.2 | 44.6 | 57.1 | 88.7 | 100.0 |
| off | cafe | -15 | 62.1 | 61.6 | 75.7 | 75.7 | 95.5 | 96.6 |
| sonora | office | +5 | 18.8 | 19.3 | 17.1 | 19.3 | 20.4 | 28.2 |
| sonora | office | -5 | 24.9 | 24.9 | 32.6 | 32.6 | 33.7 | 33.7 |
| sonora | office | -15 | 24.9 | 33.7 | 27.1 | 28.7 | 28.7 | 28.7 |
| sonora | cafe | +5 | 43.5 | 33.3 | 36.2 | 31.1 | 43.5 | 76.8 |
| sonora | cafe | -5 | 45.2 | 43.5 | 46.9 | 52.5 | 57.6 | 83.6 |
| sonora | cafe | -15 | 44.6 | 45.2 | 47.5 | 52.0 | 57.6 | 82.5 |
| fast | office | +5 | 8.8 | 9.9 | 9.4 | 8.8 | 12.2 | 25.4 |
| fast | office | -5 | 9.9 | 9.9 | 10.5 | 9.9 | 11.0 | 40.9 |
| fast | office | -15 | 13.3 | 13.3 | 12.7 | 14.4 | 14.4 | 57.5 |
| fast | cafe | +5 | 10.7 | 13.0 | 15.8 | 24.9 | 79.1 | 99.4 |
| fast | cafe | -5 | 11.9 | 12.4 | 15.8 | 23.7 | 83.6 | 99.4 |
| fast | cafe | -15 | 14.7 | 19.8 | 23.2 | 35.0 | 80.2 | 100.0 |
| thorough | office | +5 | 8.3 | 8.8 | 11.0 | 9.4 | 11.0 | 10.5 |
| thorough | office | -5 | 9.4 | 13.8 | 12.7 | 13.8 | 12.7 | 17.1 |
| thorough | office | -15 | 20.4 | 23.8 | 27.6 | 24.3 | 23.8 | 26.5 |
| thorough | cafe | +5 | 12.4 | 11.9 | 15.3 | 24.3 | 48.0 | 94.9 |
| thorough | cafe | -5 | 13.6 | 15.3 | 18.1 | 22.0 | 40.1 | 84.7 |
| thorough | cafe | -15 | 22.0 | 34.5 | 37.9 | 37.3 | 52.0 | 80.2 |
| sonora-linear | office | +5 | 9.9 | 9.9 | 11.0 | 10.5 | 11.0 | 48.1 |
| sonora-linear | office | -5 | 24.3 | 32.0 | 24.9 | 31.5 | 24.9 | 72.9 |
| sonora-linear | office | -15 | 51.9 | 47.5 | 55.8 | 51.4 | 54.7 | 85.6 |
| sonora-linear | cafe | +5 | 12.4 | 13.0 | 18.1 | 23.2 | 80.2 | 99.4 |
| sonora-linear | cafe | -5 | 24.9 | 23.7 | 32.2 | 38.4 | 79.7 | 100.0 |
| sonora-linear | cafe | -15 | 44.6 | 46.9 | 50.8 | 59.9 | 98.9 | 95.5 |
| sonora-nearend | office | +5 | 17.1 | 18.2 | 14.4 | 17.7 | 15.5 | 13.8 |
| sonora-nearend | office | -5 | 32.6 | 33.1 | 33.7 | 34.8 | 32.6 | 32.0 |
| sonora-nearend | office | -15 | 33.1 | 27.1 | 29.8 | 33.1 | 35.4 | 24.9 |
| sonora-nearend | cafe | +5 | 28.2 | 17.5 | 28.2 | 30.5 | 56.5 | 99.4 |
| sonora-nearend | cafe | -5 | 45.2 | 43.5 | 45.2 | 46.9 | 54.8 | 96.0 |
| sonora-nearend | cafe | -15 | 43.5 | 46.3 | 51.4 | 52.0 | 60.5 | 81.9 |
| sonora-gentle | office | +5 | 18.2 | 18.8 | 14.9 | 20.4 | 14.9 | 13.8 |
| sonora-gentle | office | -5 | 32.6 | 33.1 | 33.7 | 34.3 | 32.6 | 32.0 |
| sonora-gentle | office | -15 | 33.1 | 33.7 | 29.8 | 28.7 | 34.3 | 27.6 |
| sonora-gentle | cafe | +5 | 28.2 | 16.9 | 30.5 | 31.1 | 63.8 | 99.4 |
| sonora-gentle | cafe | -5 | 44.6 | 45.8 | 48.0 | 49.2 | 55.4 | 96.0 |
| sonora-gentle | cafe | -15 | 43.5 | 46.3 | 46.3 | 52.0 | 66.1 | 82.5 |
| sonora-long | office | +5 | 17.1 | 18.2 | 14.9 | 24.3 | 16.6 | 19.3 |
| sonora-long | office | -5 | 26.0 | 26.5 | 27.1 | 25.4 | 32.6 | 26.0 |
| sonora-long | office | -15 | 24.9 | 33.1 | 33.7 | 33.1 | 29.3 | 28.2 |
| sonora-long | cafe | +5 | 18.6 | 16.4 | 32.8 | 31.6 | 36.2 | 99.4 |
| sonora-long | cafe | -5 | 43.5 | 44.6 | 45.2 | 51.4 | 57.1 | 83.1 |
| sonora-long | cafe | -15 | 44.6 | 45.2 | 47.5 | 53.1 | 60.5 | 83.6 |
| sonora-long80 | office | +5 | 16.0 | 16.6 | 13.8 | 18.8 | 14.9 | 23.8 |
| sonora-long80 | office | -5 | 25.4 | 25.4 | 32.6 | 26.5 | 26.0 | 32.0 |
| sonora-long80 | office | -15 | 26.5 | 28.7 | 27.1 | 28.7 | 33.1 | 39.2 |
| sonora-long80 | cafe | +5 | 18.1 | 14.7 | 27.1 | 27.1 | 62.1 | 98.9 |
| sonora-long80 | cafe | -5 | 41.8 | 37.3 | 42.9 | 50.8 | 53.7 | 96.0 |
| sonora-long80 | cafe | -15 | 44.6 | 44.6 | 46.9 | 54.2 | 60.5 | 90.4 |
| sonora-long80-nearend | office | +5 | 15.5 | 13.8 | 13.3 | 11.6 | 14.9 | 11.0 |
| sonora-long80-nearend | office | -5 | 33.7 | 22.7 | 26.5 | 28.2 | 27.1 | 34.3 |
| sonora-long80-nearend | office | -15 | 29.8 | 32.0 | 37.6 | 33.7 | 32.6 | 41.4 |
| sonora-long80-nearend | cafe | +5 | 13.6 | 16.4 | 20.9 | 26.6 | 78.0 | 99.4 |
| sonora-long80-nearend | cafe | -5 | 36.7 | 35.6 | 32.8 | 52.0 | 76.8 | 100.0 |
| sonora-long80-nearend | cafe | -15 | 43.5 | 45.8 | 55.4 | 59.3 | 86.4 | 94.9 |

## Echo words that leaked into my text

Rows: noise type and echo level (SER: lower = louder echo). Columns: noise level (SNR: lower = louder noise).

| Remover | Noise | SER | clean | +20 dB | +10 dB | +5 dB | +0 dB | -5 dB |
|---|---|---|---|---|---|---|---|---|
| off | office | +5 | 30 | 20 | 26 | 26 | 0 | 0 |
| off | office | -5 | 26 | 28 | 26 | 26 | 26 | 0 |
| off | office | -15 | 43 | 42 | 42 | 40 | 36 | 1 |
| off | cafe | +5 | 2 | 5 | 0 | 0 | 0 | 0 |
| off | cafe | -5 | 7 | 11 | 8 | 8 | 0 | 0 |
| off | cafe | -15 | 13 | 14 | 21 | 21 | 0 | 0 |
| sonora | office | +5 | 0 | 0 | 0 | 0 | 0 | 0 |
| sonora | office | -5 | 0 | 0 | 0 | 0 | 0 | 0 |
| sonora | office | -15 | 0 | 0 | 0 | 0 | 0 | 0 |
| sonora | cafe | +5 | 0 | 0 | 0 | 0 | 2 | 0 |
| sonora | cafe | -5 | 0 | 0 | 0 | 0 | 1 | 0 |
| sonora | cafe | -15 | 0 | 0 | 0 | 0 | 1 | 0 |
| fast | office | +5 | 0 | 0 | 0 | 0 | 0 | 0 |
| fast | office | -5 | 0 | 0 | 0 | 0 | 0 | 0 |
| fast | office | -15 | 0 | 0 | 0 | 0 | 0 | 0 |
| fast | cafe | +5 | 0 | 0 | 0 | 0 | 0 | 0 |
| fast | cafe | -5 | 0 | 0 | 0 | 0 | 0 | 0 |
| fast | cafe | -15 | 0 | 0 | 0 | 0 | 0 | 0 |
| thorough | office | +5 | 0 | 0 | 0 | 0 | 0 | 0 |
| thorough | office | -5 | 0 | 0 | 0 | 0 | 0 | 0 |
| thorough | office | -15 | 0 | 0 | 0 | 0 | 0 | 0 |
| thorough | cafe | +5 | 0 | 0 | 0 | 0 | 0 | 0 |
| thorough | cafe | -5 | 0 | 0 | 0 | 0 | 1 | 0 |
| thorough | cafe | -15 | 0 | 0 | 0 | 0 | 1 | 0 |
| sonora-linear | office | +5 | 0 | 0 | 0 | 0 | 0 | 0 |
| sonora-linear | office | -5 | 10 | 7 | 8 | 7 | 0 | 0 |
| sonora-linear | office | -15 | 15 | 16 | 20 | 16 | 17 | 0 |
| sonora-linear | cafe | +5 | 0 | 0 | 0 | 0 | 0 | 0 |
| sonora-linear | cafe | -5 | 1 | 1 | 0 | 0 | 0 | 0 |
| sonora-linear | cafe | -15 | 0 | 0 | 0 | 5 | 0 | 0 |
| sonora-nearend | office | +5 | 0 | 0 | 0 | 0 | 0 | 0 |
| sonora-nearend | office | -5 | 0 | 0 | 0 | 0 | 0 | 0 |
| sonora-nearend | office | -15 | 0 | 0 | 0 | 0 | 0 | 0 |
| sonora-nearend | cafe | +5 | 0 | 0 | 0 | 0 | 1 | 0 |
| sonora-nearend | cafe | -5 | 0 | 0 | 0 | 0 | 1 | 0 |
| sonora-nearend | cafe | -15 | 0 | 0 | 0 | 0 | 1 | 0 |
| sonora-gentle | office | +5 | 0 | 0 | 0 | 0 | 0 | 0 |
| sonora-gentle | office | -5 | 0 | 0 | 0 | 0 | 0 | 0 |
| sonora-gentle | office | -15 | 0 | 0 | 0 | 0 | 0 | 0 |
| sonora-gentle | cafe | +5 | 0 | 0 | 1 | 0 | 0 | 0 |
| sonora-gentle | cafe | -5 | 0 | 0 | 0 | 0 | 1 | 0 |
| sonora-gentle | cafe | -15 | 0 | 0 | 0 | 0 | 0 | 0 |
| sonora-long | office | +5 | 0 | 0 | 0 | 0 | 0 | 0 |
| sonora-long | office | -5 | 0 | 0 | 0 | 0 | 0 | 0 |
| sonora-long | office | -15 | 0 | 0 | 0 | 0 | 0 | 0 |
| sonora-long | cafe | +5 | 0 | 0 | 0 | 2 | 1 | 0 |
| sonora-long | cafe | -5 | 0 | 0 | 0 | 0 | 1 | 0 |
| sonora-long | cafe | -15 | 0 | 0 | 0 | 0 | 1 | 0 |
| sonora-long80 | office | +5 | 0 | 0 | 0 | 0 | 0 | 0 |
| sonora-long80 | office | -5 | 0 | 0 | 0 | 0 | 0 | 0 |
| sonora-long80 | office | -15 | 0 | 0 | 0 | 0 | 0 | 0 |
| sonora-long80 | cafe | +5 | 0 | 0 | 0 | 0 | 0 | 0 |
| sonora-long80 | cafe | -5 | 0 | 0 | 0 | 0 | 1 | 0 |
| sonora-long80 | cafe | -15 | 0 | 0 | 0 | 0 | 1 | 0 |
| sonora-long80-nearend | office | +5 | 0 | 0 | 0 | 0 | 0 | 0 |
| sonora-long80-nearend | office | -5 | 0 | 0 | 0 | 0 | 0 | 0 |
| sonora-long80-nearend | office | -15 | 0 | 0 | 0 | 0 | 0 | 10 |
| sonora-long80-nearend | cafe | +5 | 0 | 0 | 0 | 0 | 0 | 0 |
| sonora-long80-nearend | cafe | -5 | 0 | 0 | 0 | 0 | 0 | 0 |
| sonora-long80-nearend | cafe | -15 | 0 | 0 | 6 | 4 | 0 | 0 |

## Echo removed in far-end-only parts (dB)

Rows: noise type and echo level (SER: lower = louder echo). Columns: noise level (SNR: lower = louder noise).

| Remover | Noise | SER | clean | +20 dB | +10 dB | +5 dB | +0 dB | -5 dB |
|---|---|---|---|---|---|---|---|---|
| off | office | +5 | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 |
| off | office | -5 | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 |
| off | office | -15 | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 |
| off | cafe | +5 | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 |
| off | cafe | -5 | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 |
| off | cafe | -15 | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 |
| sonora | office | +5 | 39.4 | 22.6 | 14.6 | 7.4 | 7.4 | 5.5 |
| sonora | office | -5 | 33.5 | 29.9 | 23.2 | 15.4 | 14.0 | 9.7 |
| sonora | office | -15 | 42.7 | 37.6 | 28.5 | 25.5 | 21.9 | 18.3 |
| sonora | cafe | +5 | 29.7 | 15.8 | 7.0 | 4.1 | 2.5 | 2.2 |
| sonora | cafe | -5 | 26.6 | 19.8 | 14.1 | 10.7 | 7.4 | 4.5 |
| sonora | cafe | -15 | 25.6 | 22.9 | 18.8 | 16.6 | 13.9 | 10.7 |
| fast | office | +5 | 27.0 | 14.6 | 7.6 | 2.3 | 1.9 | 0.5 |
| fast | office | -5 | 27.2 | 19.8 | 14.4 | 7.5 | 7.9 | 4.0 |
| fast | office | -15 | 27.3 | 24.4 | 19.5 | 14.0 | 14.6 | 11.1 |
| fast | cafe | +5 | 21.6 | 10.3 | 3.6 | 1.8 | 0.6 | 0.2 |
| fast | cafe | -5 | 22.1 | 13.7 | 9.5 | 7.1 | 4.0 | 1.9 |
| fast | cafe | -15 | 21.9 | 17.5 | 13.3 | 12.2 | 9.9 | 7.2 |
| thorough | office | +5 | 47.7 | 27.1 | 16.5 | 12.7 | 9.0 | 7.3 |
| thorough | office | -5 | 46.5 | 37.0 | 27.4 | 21.9 | 16.9 | 11.6 |
| thorough | office | -15 | 45.8 | 42.8 | 37.5 | 31.2 | 27.4 | 21.3 |
| thorough | cafe | +5 | 42.9 | 17.5 | 7.1 | 4.1 | 2.4 | 1.5 |
| thorough | cafe | -5 | 42.4 | 27.8 | 16.7 | 11.8 | 7.2 | 4.2 |
| thorough | cafe | -15 | 43.3 | 37.0 | 27.2 | 22.8 | 16.9 | 11.9 |
| sonora-linear | office | +5 | 3.8 | 3.3 | 2.3 | 1.2 | 0.6 | 0.3 |
| sonora-linear | office | -5 | 3.8 | 3.9 | 3.5 | 2.9 | 3.1 | 1.9 |
| sonora-linear | office | -15 | 4.1 | 4.0 | 3.9 | 4.0 | 4.0 | 3.6 |
| sonora-linear | cafe | +5 | 3.0 | 2.7 | 1.6 | 0.7 | 0.3 | 0.1 |
| sonora-linear | cafe | -5 | 3.2 | 3.4 | 2.8 | 2.7 | 1.8 | 0.8 |
| sonora-linear | cafe | -15 | 3.2 | 3.4 | 3.2 | 3.4 | 2.8 | 2.5 |
| sonora-nearend | office | +5 | 35.2 | 20.3 | 12.8 | 5.1 | 6.2 | 4.2 |
| sonora-nearend | office | -5 | 18.3 | 18.9 | 17.3 | 12.2 | 12.8 | 7.6 |
| sonora-nearend | office | -15 | 15.9 | 16.0 | 16.2 | 15.1 | 14.8 | 15.6 |
| sonora-nearend | cafe | +5 | 16.5 | 12.3 | 5.0 | 2.8 | 1.4 | 1.0 |
| sonora-nearend | cafe | -5 | 14.4 | 12.2 | 10.7 | 7.7 | 5.3 | 2.8 |
| sonora-nearend | cafe | -15 | 13.8 | 12.7 | 11.4 | 10.0 | 9.0 | 7.6 |
| sonora-gentle | office | +5 | 30.7 | 20.1 | 12.8 | 5.1 | 6.1 | 4.2 |
| sonora-gentle | office | -5 | 18.2 | 18.7 | 17.2 | 12.1 | 12.7 | 7.5 |
| sonora-gentle | office | -15 | 15.7 | 15.9 | 15.9 | 14.9 | 14.5 | 15.6 |
| sonora-gentle | cafe | +5 | 16.3 | 12.1 | 4.8 | 2.7 | 1.4 | 1.0 |
| sonora-gentle | cafe | -5 | 14.1 | 12.0 | 10.4 | 7.6 | 5.2 | 2.8 |
| sonora-gentle | cafe | -15 | 13.5 | 12.5 | 11.2 | 9.8 | 8.8 | 7.5 |
| sonora-long | office | +5 | 19.8 | 19.6 | 13.7 | 5.4 | 6.3 | 5.3 |
| sonora-long | office | -5 | 30.9 | 18.9 | 20.5 | 11.3 | 12.7 | 8.8 |
| sonora-long | office | -15 | 18.8 | 18.6 | 24.7 | 17.6 | 17.5 | 15.3 |
| sonora-long | cafe | +5 | 30.3 | 13.4 | 5.8 | 3.2 | 1.9 | 0.8 |
| sonora-long | cafe | -5 | 29.1 | 17.3 | 12.8 | 9.3 | 6.3 | 3.6 |
| sonora-long | cafe | -15 | 25.9 | 20.6 | 13.4 | 14.9 | 12.7 | 10.0 |
| sonora-long80 | office | +5 | 15.2 | 14.7 | 10.4 | 3.2 | 5.8 | 4.9 |
| sonora-long80 | office | -5 | 15.0 | 15.6 | 15.7 | 6.3 | 10.1 | 6.3 |
| sonora-long80 | office | -15 | 14.8 | 14.2 | 19.3 | 15.7 | 16.1 | 11.1 |
| sonora-long80 | cafe | +5 | 17.1 | 9.8 | 4.6 | 2.5 | 1.2 | 0.7 |
| sonora-long80 | cafe | -5 | 10.9 | 10.9 | 8.7 | 6.7 | 5.1 | 2.5 |
| sonora-long80 | cafe | -15 | 11.6 | 11.7 | 10.6 | 8.7 | 9.1 | 7.9 |
| sonora-long80-nearend | office | +5 | 11.7 | 8.1 | 5.6 | 2.0 | 3.3 | 2.6 |
| sonora-long80-nearend | office | -5 | 7.8 | 6.6 | 5.8 | 4.7 | 6.2 | 3.7 |
| sonora-long80-nearend | office | -15 | 7.1 | 6.9 | 6.4 | 5.9 | 6.3 | 5.5 |
| sonora-long80-nearend | cafe | +5 | 9.3 | 6.5 | 3.1 | 1.4 | 0.6 | 0.6 |
| sonora-long80-nearend | cafe | -5 | 7.8 | 7.0 | 6.4 | 4.6 | 2.8 | 1.0 |
| sonora-long80-nearend | cafe | -15 | 7.8 | 7.4 | 6.5 | 6.1 | 5.2 | 4.9 |

## My voice lost where there was no echo (dB, 0 is best)

Rows: noise type and echo level (SER: lower = louder echo). Columns: noise level (SNR: lower = louder noise).

| Remover | Noise | SER | clean | +20 dB | +10 dB | +5 dB | +0 dB | -5 dB |
|---|---|---|---|---|---|---|---|---|
| off | office | +5 | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 |
| off | office | -5 | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 |
| off | office | -15 | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 |
| off | cafe | +5 | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 |
| off | cafe | -5 | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 |
| off | cafe | -15 | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 |
| sonora | office | +5 | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 |
| sonora | office | -5 | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 |
| sonora | office | -15 | 0.0 | 0.0 | 0.0 | -0.0 | -0.0 | -0.0 |
| sonora | cafe | +5 | 0.0 | 0.0 | 0.0 | 0.0 | -0.0 | -0.0 |
| sonora | cafe | -5 | 0.0 | 0.0 | 0.0 | -0.0 | -0.0 | -0.0 |
| sonora | cafe | -15 | 0.0 | -0.0 | -0.0 | -0.0 | -0.0 | -0.0 |
| fast | office | +5 | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | -0.0 |
| fast | office | -5 | 0.0 | 0.0 | 0.0 | 0.0 | -0.0 | -0.0 |
| fast | office | -15 | 0.0 | 0.0 | -0.0 | -0.0 | -0.0 | -0.0 |
| fast | cafe | +5 | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 |
| fast | cafe | -5 | 0.0 | 0.0 | -0.0 | 0.0 | 0.0 | 0.0 |
| fast | cafe | -15 | 0.0 | 0.0 | -0.0 | 0.0 | 0.0 | 0.0 |
| thorough | office | +5 | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 |
| thorough | office | -5 | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 |
| thorough | office | -15 | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 |
| thorough | cafe | +5 | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 |
| thorough | cafe | -5 | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 |
| thorough | cafe | -15 | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 |
| sonora-linear | office | +5 | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 |
| sonora-linear | office | -5 | 0.0 | -0.0 | 0.0 | -0.0 | 0.0 | -0.0 |
| sonora-linear | office | -15 | 0.0 | -0.0 | 0.0 | -0.0 | 0.0 | -0.0 |
| sonora-linear | cafe | +5 | 0.0 | 0.0 | 0.0 | 0.0 | -0.0 | 0.0 |
| sonora-linear | cafe | -5 | 0.0 | -0.0 | -0.0 | -0.0 | -0.0 | 0.0 |
| sonora-linear | cafe | -15 | 0.0 | -0.0 | -0.0 | -0.0 | -0.0 | 0.0 |
| sonora-nearend | office | +5 | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 |
| sonora-nearend | office | -5 | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 |
| sonora-nearend | office | -15 | 0.0 | 0.0 | 0.0 | -0.0 | -0.0 | -0.0 |
| sonora-nearend | cafe | +5 | 0.0 | 0.0 | 0.0 | 0.0 | -0.0 | -0.0 |
| sonora-nearend | cafe | -5 | 0.0 | -0.0 | -0.0 | -0.0 | -0.0 | -0.0 |
| sonora-nearend | cafe | -15 | 0.0 | -0.0 | -0.0 | -0.0 | -0.0 | -0.0 |
| sonora-gentle | office | +5 | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 |
| sonora-gentle | office | -5 | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 |
| sonora-gentle | office | -15 | 0.0 | 0.0 | 0.0 | -0.0 | -0.0 | -0.0 |
| sonora-gentle | cafe | +5 | 0.0 | 0.0 | 0.0 | 0.0 | -0.0 | -0.0 |
| sonora-gentle | cafe | -5 | 0.0 | -0.0 | -0.0 | -0.0 | -0.0 | -0.0 |
| sonora-gentle | cafe | -15 | 0.0 | -0.0 | -0.0 | -0.0 | -0.0 | -0.0 |
| sonora-long | office | +5 | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 |
| sonora-long | office | -5 | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 |
| sonora-long | office | -15 | 0.0 | 0.0 | 0.0 | -0.0 | -0.0 | -0.0 |
| sonora-long | cafe | +5 | 0.0 | 0.0 | 0.0 | -0.0 | -0.0 | -0.0 |
| sonora-long | cafe | -5 | 0.0 | -0.0 | -0.0 | -0.0 | -0.0 | -0.0 |
| sonora-long | cafe | -15 | 0.0 | -0.0 | -0.0 | -0.0 | -0.0 | -0.0 |
| sonora-long80 | office | +5 | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 |
| sonora-long80 | office | -5 | 0.0 | 0.0 | 0.0 | -0.0 | -0.0 | -0.0 |
| sonora-long80 | office | -15 | 0.0 | 0.0 | 0.0 | -0.0 | -0.0 | -0.0 |
| sonora-long80 | cafe | +5 | 0.0 | 0.0 | 0.0 | -0.0 | -0.0 | -0.0 |
| sonora-long80 | cafe | -5 | 0.0 | -0.0 | -0.0 | -0.0 | -0.0 | -0.0 |
| sonora-long80 | cafe | -15 | 0.0 | -0.0 | -0.0 | -0.0 | -0.0 | -0.0 |
| sonora-long80-nearend | office | +5 | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 |
| sonora-long80-nearend | office | -5 | 0.0 | 0.0 | 0.0 | -0.0 | -0.0 | -0.0 |
| sonora-long80-nearend | office | -15 | 0.0 | 0.0 | 0.0 | -0.0 | -0.0 | -0.0 |
| sonora-long80-nearend | cafe | +5 | 0.0 | 0.0 | 0.0 | -0.0 | -0.0 | -0.0 |
| sonora-long80-nearend | cafe | -5 | 0.0 | -0.0 | -0.0 | -0.0 | -0.0 | -0.0 |
| sonora-long80-nearend | cafe | -15 | 0.0 | -0.0 | -0.0 | -0.0 | -0.0 | -0.0 |

## Double talk: my voice level after echo removal (dB, 0 is best)

Rows: noise type and echo level (SER: lower = louder echo). Columns: noise level (SNR: lower = louder noise).

| Remover | Noise | SER | clean | +20 dB | +10 dB | +5 dB | +0 dB | -5 dB |
|---|---|---|---|---|---|---|---|---|
| off | office | +5 | -0.0 | -0.0 | -0.0 | -0.0 | -0.0 | -0.0 |
| off | office | -5 | -0.0 | -0.0 | -0.0 | -0.0 | -0.0 | -0.1 |
| off | office | -15 | -0.1 | -0.1 | -0.1 | -0.1 | -0.1 | -0.2 |
| off | cafe | +5 | -0.0 | -0.0 | -0.0 | 0.0 | -0.0 | -0.0 |
| off | cafe | -5 | -0.1 | -0.1 | -0.0 | -0.0 | -0.1 | -0.1 |
| off | cafe | -15 | -0.2 | -0.2 | -0.2 | -0.2 | -0.2 | -0.2 |
| sonora | office | +5 | -3.3 | -2.7 | -1.8 | -1.7 | -2.0 | -2.3 |
| sonora | office | -5 | -4.8 | -4.3 | -4.1 | -4.1 | -3.8 | -4.2 |
| sonora | office | -15 | -6.0 | -5.8 | -5.5 | -5.1 | -5.0 | -5.2 |
| sonora | cafe | +5 | -7.6 | -4.5 | -4.0 | -1.9 | -1.0 | -0.5 |
| sonora | cafe | -5 | -11.7 | -8.1 | -7.5 | -7.0 | -6.6 | -5.9 |
| sonora | cafe | -15 | -17.5 | -14.9 | -13.3 | -12.8 | -11.1 | -10.2 |
| fast | office | +5 | 0.0 | 0.0 | -0.0 | -0.0 | -0.0 | -0.0 |
| fast | office | -5 | 0.0 | 0.0 | -0.0 | 0.0 | -0.0 | -0.0 |
| fast | office | -15 | 0.0 | -0.0 | -0.1 | -0.0 | -0.0 | -0.1 |
| fast | cafe | +5 | -0.0 | -0.0 | -0.0 | 0.0 | -0.1 | -0.1 |
| fast | cafe | -5 | -0.1 | -0.1 | -0.1 | -0.0 | -0.1 | -0.1 |
| fast | cafe | -15 | -0.1 | -0.1 | -0.0 | -0.1 | -0.2 | -0.1 |
| thorough | office | +5 | -0.5 | -0.3 | -0.2 | -0.2 | -0.3 | -0.3 |
| thorough | office | -5 | -1.1 | -0.8 | -0.7 | -0.8 | -0.8 | -0.9 |
| thorough | office | -15 | -2.7 | -2.2 | -2.2 | -2.4 | -2.2 | -2.3 |
| thorough | cafe | +5 | -0.5 | -0.5 | -0.6 | -0.5 | -0.7 | -0.6 |
| thorough | cafe | -5 | -2.0 | -2.2 | -2.1 | -2.0 | -2.0 | -1.7 |
| thorough | cafe | -15 | -4.5 | -5.8 | -5.7 | -5.4 | -5.1 | -4.2 |
| sonora-linear | office | +5 | -0.3 | -0.3 | -0.2 | -0.2 | -0.2 | -0.2 |
| sonora-linear | office | -5 | -0.2 | -0.2 | -0.3 | -0.3 | -0.2 | -0.2 |
| sonora-linear | office | -15 | -0.2 | -0.2 | -0.3 | -0.2 | -0.3 | -0.3 |
| sonora-linear | cafe | +5 | -0.3 | -0.3 | -0.4 | -0.3 | -0.3 | -0.3 |
| sonora-linear | cafe | -5 | -0.3 | -0.3 | -0.4 | -0.3 | -0.2 | -0.3 |
| sonora-linear | cafe | -15 | -0.1 | -0.1 | -0.2 | -0.2 | -0.2 | -0.0 |
| sonora-nearend | office | +5 | -2.2 | -1.8 | -1.2 | -1.1 | -1.1 | -1.2 |
| sonora-nearend | office | -5 | -3.2 | -2.7 | -2.6 | -2.6 | -2.3 | -3.0 |
| sonora-nearend | office | -15 | -4.8 | -4.7 | -4.4 | -4.2 | -4.0 | -4.3 |
| sonora-nearend | cafe | +5 | -4.1 | -2.5 | -1.9 | -1.0 | -0.4 | -0.3 |
| sonora-nearend | cafe | -5 | -6.7 | -4.8 | -4.2 | -3.4 | -2.6 | -2.1 |
| sonora-nearend | cafe | -15 | -12.5 | -10.0 | -9.2 | -8.6 | -6.8 | -6.3 |
| sonora-gentle | office | +5 | -2.2 | -1.8 | -1.1 | -1.0 | -1.1 | -1.2 |
| sonora-gentle | office | -5 | -3.2 | -2.6 | -2.5 | -2.5 | -2.2 | -2.9 |
| sonora-gentle | office | -15 | -4.8 | -4.7 | -4.3 | -4.1 | -3.9 | -4.1 |
| sonora-gentle | cafe | +5 | -4.0 | -2.5 | -1.8 | -1.0 | -0.4 | -0.3 |
| sonora-gentle | cafe | -5 | -6.4 | -4.5 | -3.9 | -3.3 | -2.5 | -2.1 |
| sonora-gentle | cafe | -15 | -12.2 | -9.5 | -8.6 | -8.2 | -6.5 | -6.1 |
| sonora-long | office | +5 | -2.9 | -2.2 | -1.3 | -1.4 | -1.4 | -1.6 |
| sonora-long | office | -5 | -4.2 | -3.8 | -3.3 | -3.1 | -2.7 | -3.4 |
| sonora-long | office | -15 | -5.5 | -5.6 | -5.1 | -4.9 | -4.8 | -4.6 |
| sonora-long | cafe | +5 | -4.1 | -2.2 | -2.0 | -1.1 | -0.2 | -0.1 |
| sonora-long | cafe | -5 | -11.0 | -7.0 | -6.8 | -5.6 | -4.0 | -1.9 |
| sonora-long | cafe | -15 | -17.6 | -15.3 | -13.9 | -12.6 | -11.8 | -10.1 |
| sonora-long80 | office | +5 | -2.8 | -1.8 | -1.0 | -1.0 | -1.1 | -1.2 |
| sonora-long80 | office | -5 | -4.0 | -3.0 | -2.2 | -2.1 | -1.8 | -2.8 |
| sonora-long80 | office | -15 | -5.6 | -5.4 | -4.9 | -4.4 | -4.4 | -3.8 |
| sonora-long80 | cafe | +5 | -2.2 | -0.9 | -0.9 | -0.5 | -0.2 | -0.1 |
| sonora-long80 | cafe | -5 | -6.3 | -2.9 | -2.9 | -1.9 | -1.3 | -0.4 |
| sonora-long80 | cafe | -15 | -14.0 | -10.0 | -7.6 | -5.9 | -6.0 | -4.5 |
| sonora-long80-nearend | office | +5 | -1.9 | -1.1 | -0.5 | -0.6 | -0.6 | -0.4 |
| sonora-long80-nearend | office | -5 | -2.2 | -1.4 | -0.9 | -1.0 | -0.8 | -1.7 |
| sonora-long80-nearend | office | -15 | -3.7 | -3.2 | -2.7 | -2.2 | -2.3 | -1.8 |
| sonora-long80-nearend | cafe | +5 | -1.0 | -0.5 | -0.6 | -0.5 | -0.1 | -0.1 |
| sonora-long80-nearend | cafe | -5 | -2.1 | -1.1 | -0.9 | -0.7 | -0.4 | -0.2 |
| sonora-long80-nearend | cafe | -15 | -5.0 | -3.6 | -2.5 | -1.9 | -2.0 | -1.5 |

## Double talk: my voice cleaner by (SI-SDR dB, higher is better)

Rows: noise type and echo level (SER: lower = louder echo). Columns: noise level (SNR: lower = louder noise).

| Remover | Noise | SER | clean | +20 dB | +10 dB | +5 dB | +0 dB | -5 dB |
|---|---|---|---|---|---|---|---|---|
| off | office | +5 | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 |
| off | office | -5 | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 |
| off | office | -15 | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 |
| off | cafe | +5 | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 |
| off | cafe | -5 | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 |
| off | cafe | -15 | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 |
| sonora | office | +5 | -1.1 | -0.2 | 2.0 | 1.9 | 1.0 | 4.8 |
| sonora | office | -5 | 6.7 | 7.1 | 7.1 | 6.5 | 4.7 | -2.0 |
| sonora | office | -15 | 14.6 | 14.5 | 15.2 | 13.9 | 12.3 | 4.9 |
| sonora | cafe | +5 | -5.2 | -2.2 | -1.4 | 0.2 | 0.1 | -0.2 |
| sonora | cafe | -5 | 1.0 | 3.1 | 3.2 | 1.6 | 0.3 | -1.8 |
| sonora | cafe | -15 | 7.3 | 7.8 | 6.0 | 5.6 | 4.3 | 1.3 |
| fast | office | +5 | 9.8 | 6.3 | 1.8 | 3.0 | 1.2 | 0.0 |
| fast | office | -5 | 14.3 | 12.4 | 7.5 | 9.2 | 6.2 | 0.7 |
| fast | office | -15 | 17.1 | 16.4 | 14.2 | 14.7 | 13.0 | 4.5 |
| fast | cafe | +5 | 8.6 | 7.3 | 3.9 | 1.5 | 0.6 | 0.2 |
| fast | cafe | -5 | 12.1 | 11.3 | 9.7 | 6.7 | 4.1 | 1.8 |
| fast | cafe | -15 | 17.3 | 14.9 | 14.2 | 12.7 | 10.7 | 7.5 |
| thorough | office | +5 | 9.1 | 11.1 | 10.1 | 9.2 | 7.7 | 8.7 |
| thorough | office | -5 | 14.5 | 15.5 | 15.0 | 14.2 | 12.2 | 9.9 |
| thorough | office | -15 | 20.1 | 20.9 | 20.7 | 20.1 | 19.0 | 14.4 |
| thorough | cafe | +5 | 8.3 | 7.8 | 5.0 | 2.6 | 1.7 | 1.0 |
| thorough | cafe | -5 | 11.9 | 11.4 | 10.3 | 7.4 | 4.9 | 2.3 |
| thorough | cafe | -15 | 17.7 | 16.2 | 15.6 | 13.5 | 11.0 | 7.1 |
| sonora-linear | office | +5 | 1.5 | 1.4 | 0.5 | 0.8 | -0.1 | -0.1 |
| sonora-linear | office | -5 | 4.1 | 4.1 | 3.2 | 3.3 | 2.7 | 0.1 |
| sonora-linear | office | -15 | 4.0 | 4.0 | 3.8 | 3.8 | 3.8 | 1.9 |
| sonora-linear | cafe | +5 | 0.5 | -1.1 | -0.6 | 0.1 | 0.1 | -0.2 |
| sonora-linear | cafe | -5 | 2.9 | 2.8 | 2.3 | 1.9 | 1.8 | 0.6 |
| sonora-linear | cafe | -15 | 3.1 | 3.5 | 3.5 | 2.9 | 2.9 | 3.1 |
| sonora-nearend | office | +5 | 0.5 | 1.0 | 1.7 | 2.3 | 1.3 | 4.5 |
| sonora-nearend | office | -5 | 7.9 | 8.2 | 6.0 | 7.4 | 5.3 | -1.1 |
| sonora-nearend | office | -15 | 10.6 | 9.9 | 10.6 | 10.5 | 9.9 | 2.7 |
| sonora-nearend | cafe | +5 | -2.5 | -0.3 | 0.2 | 0.4 | 0.2 | -0.1 |
| sonora-nearend | cafe | -5 | 2.5 | 3.4 | 3.3 | 2.4 | 1.9 | 0.4 |
| sonora-nearend | cafe | -15 | 1.3 | 2.6 | 3.9 | 2.4 | 2.2 | 2.1 |
| sonora-gentle | office | +5 | 0.6 | 1.1 | 1.8 | 2.3 | 1.3 | 4.5 |
| sonora-gentle | office | -5 | 7.9 | 8.2 | 6.1 | 7.5 | 5.3 | -1.0 |
| sonora-gentle | office | -15 | 10.5 | 9.9 | 10.7 | 10.5 | 9.9 | 2.8 |
| sonora-gentle | cafe | +5 | -2.3 | -0.2 | 0.3 | 0.4 | 0.2 | -0.1 |
| sonora-gentle | cafe | -5 | 2.8 | 3.6 | 3.5 | 2.5 | 2.0 | 0.4 |
| sonora-gentle | cafe | -15 | 1.4 | 2.9 | 4.1 | 2.5 | 2.4 | 2.2 |
| sonora-long | office | +5 | -0.5 | 0.7 | 2.4 | 2.4 | 1.5 | 3.3 |
| sonora-long | office | -5 | 7.5 | 7.7 | 8.4 | 7.6 | 5.6 | -0.7 |
| sonora-long | office | -15 | 15.7 | 15.7 | 15.1 | 14.2 | 12.8 | 9.9 |
| sonora-long | cafe | +5 | -2.3 | 0.7 | 0.5 | 0.7 | 0.2 | 0.1 |
| sonora-long | cafe | -5 | 1.7 | 3.2 | 3.6 | 2.6 | 2.1 | 0.5 |
| sonora-long | cafe | -15 | 4.3 | 5.0 | 3.0 | 3.9 | 2.8 | 1.7 |
| sonora-long80 | office | +5 | -0.4 | 1.1 | 0.7 | 2.1 | 1.4 | 3.4 |
| sonora-long80 | office | -5 | 7.3 | 7.9 | 5.3 | 6.9 | 4.7 | -0.9 |
| sonora-long80 | office | -15 | 14.8 | 14.9 | 13.8 | 11.6 | 10.7 | 4.8 |
| sonora-long80 | cafe | +5 | -0.6 | 1.7 | 0.5 | 0.2 | 0.2 | -0.0 |
| sonora-long80 | cafe | -5 | 1.7 | 2.5 | 2.7 | 1.8 | 1.3 | -0.1 |
| sonora-long80 | cafe | -15 | -0.2 | -0.5 | 0.3 | 1.0 | 1.0 | 1.2 |
| sonora-long80-nearend | office | +5 | 0.5 | 1.6 | 0.7 | 1.4 | 1.0 | 3.6 |
| sonora-long80-nearend | office | -5 | 7.3 | 6.4 | 4.3 | 5.2 | 3.2 | -0.5 |
| sonora-long80-nearend | office | -15 | 7.2 | 7.8 | 7.5 | 6.5 | 6.0 | 3.2 |
| sonora-long80-nearend | cafe | +5 | 0.5 | 1.7 | 0.2 | 0.0 | 0.1 | -0.0 |
| sonora-long80-nearend | cafe | -5 | 2.3 | 2.6 | 2.3 | 1.6 | 1.3 | -0.1 |
| sonora-long80-nearend | cafe | -15 | 0.5 | 1.6 | 2.1 | 2.0 | 1.7 | 2.3 |

## Where each remover stops working

The noise level at which my WER first passes 1.5 × its clean value + 5 points. "–" means it held to −5 dB.

| Remover | Noise | SER | Breaks at SNR |
|---|---|---|---|
| off | office | +5 | -5 dB |
| off | office | -5 | – |
| off | office | -15 | – |
| off | cafe | +5 | +0 dB |
| off | cafe | -5 | +0 dB |
| off | cafe | -15 | – |
| sonora | office | +5 | – |
| sonora | office | -5 | – |
| sonora | office | -15 | – |
| sonora | cafe | +5 | -5 dB |
| sonora | cafe | -5 | -5 dB |
| sonora | cafe | -15 | -5 dB |
| fast | office | +5 | -5 dB |
| fast | office | -5 | -5 dB |
| fast | office | -15 | -5 dB |
| fast | cafe | +5 | +5 dB |
| fast | cafe | -5 | +5 dB |
| fast | cafe | -15 | +5 dB |
| thorough | office | +5 | – |
| thorough | office | -5 | – |
| thorough | office | -15 | – |
| thorough | cafe | +5 | +5 dB |
| thorough | cafe | -5 | +0 dB |
| thorough | cafe | -15 | +0 dB |
| sonora-linear | office | +5 | -5 dB |
| sonora-linear | office | -5 | -5 dB |
| sonora-linear | office | -15 | -5 dB |
| sonora-linear | cafe | +5 | +0 dB |
| sonora-linear | cafe | -5 | +0 dB |
| sonora-linear | cafe | -15 | +0 dB |
| sonora-nearend | office | +5 | – |
| sonora-nearend | office | -5 | – |
| sonora-nearend | office | -15 | – |
| sonora-nearend | cafe | +5 | +0 dB |
| sonora-nearend | cafe | -5 | -5 dB |
| sonora-nearend | cafe | -15 | -5 dB |
| sonora-gentle | office | +5 | – |
| sonora-gentle | office | -5 | – |
| sonora-gentle | office | -15 | – |
| sonora-gentle | cafe | +5 | +0 dB |
| sonora-gentle | cafe | -5 | -5 dB |
| sonora-gentle | cafe | -15 | -5 dB |
| sonora-long | office | +5 | – |
| sonora-long | office | -5 | – |
| sonora-long | office | -15 | – |
| sonora-long | cafe | +5 | +0 dB |
| sonora-long | cafe | -5 | -5 dB |
| sonora-long | cafe | -15 | -5 dB |
| sonora-long80 | office | +5 | – |
| sonora-long80 | office | -5 | – |
| sonora-long80 | office | -15 | – |
| sonora-long80 | cafe | +5 | +0 dB |
| sonora-long80 | cafe | -5 | -5 dB |
| sonora-long80 | cafe | -15 | -5 dB |
| sonora-long80-nearend | office | +5 | – |
| sonora-long80-nearend | office | -5 | – |
| sonora-long80-nearend | office | -15 | – |
| sonora-long80-nearend | cafe | +5 | +5 dB |
| sonora-long80-nearend | cafe | -5 | +0 dB |
| sonora-long80-nearend | cafe | -15 | +0 dB |

## Other noise (SER −5)

| Remover | Noise | SNR | My WER % | Echo words |
|---|---|---|---|---|
| off | living | 0 | 93.2 | 0 |
| off | living | 10 | 47.5 | 7 |
| off | street | 0 | 63.0 | 22 |
| off | street | 10 | 55.8 | 26 |
| sonora | living | 0 | 49.7 | 0 |
| sonora | living | 10 | 43.5 | 0 |
| sonora | street | 0 | 34.3 | 0 |
| sonora | street | 10 | 33.7 | 0 |
| fast | living | 0 | 52.5 | 0 |
| fast | living | 10 | 14.7 | 0 |
| fast | street | 0 | 19.9 | 0 |
| fast | street | 10 | 11.0 | 0 |
| thorough | living | 0 | 27.1 | 0 |
| thorough | living | 10 | 14.7 | 0 |
| thorough | street | 0 | 21.0 | 0 |
| thorough | street | 10 | 9.4 | 0 |
| sonora-linear | living | 0 | 80.8 | 0 |
| sonora-linear | living | 10 | 29.4 | 1 |
| sonora-linear | street | 0 | 32.6 | 0 |
| sonora-linear | street | 10 | 24.9 | 0 |
| sonora-nearend | living | 0 | 50.8 | 0 |
| sonora-nearend | living | 10 | 44.6 | 0 |
| sonora-nearend | street | 0 | 30.4 | 0 |
| sonora-nearend | street | 10 | 29.3 | 0 |
| sonora-gentle | living | 0 | 51.4 | 0 |
| sonora-gentle | living | 10 | 45.2 | 1 |
| sonora-gentle | street | 0 | 26.5 | 0 |
| sonora-gentle | street | 10 | 30.9 | 0 |
| sonora-long | living | 0 | 45.8 | 0 |
| sonora-long | living | 10 | 44.6 | 0 |
| sonora-long | street | 0 | 28.2 | 0 |
| sonora-long | street | 10 | 23.8 | 0 |
| sonora-long80 | living | 0 | 50.3 | 0 |
| sonora-long80 | living | 10 | 47.5 | 0 |
| sonora-long80 | street | 0 | 32.6 | 0 |
| sonora-long80 | street | 10 | 22.7 | 0 |
| sonora-long80-nearend | living | 0 | 73.4 | 0 |
| sonora-long80-nearend | living | 10 | 42.9 | 1 |
| sonora-long80-nearend | street | 0 | 34.3 | 0 |
| sonora-long80-nearend | street | 10 | 22.7 | 0 |
