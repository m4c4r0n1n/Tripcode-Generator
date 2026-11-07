# Tripcode Generator

Tripcode generator and tester. Type the word you want in your trip, hit Start, and it finds passwords that give you tripcodes containing the word. It runs right in your browser and nothing gets sent anywhere.

It generates classic DES-based tripcodes (name#password). Secure trips (name##password) won't match.
If you have any questions feel free to ask!


Use it here: https://m4c4r0n1n.github.io/Tripcode-Generator/

Or just download index.html and open it.

### Update
Added CUDA support for 30-50 series Nvidia GPUs, if you do not have one you can use the .io site here, make sure webgpu is enabled in about:config. Removed the GPU workgroups slider. The search now sets the batch size automatically, so each batch takes about 120 ms on your GPU. The slider only made batches bigger, which was the only way to get past the browser limit of 10 batches per second. The search now keeps 3 batches queued, so the GPU does not wait between batches. Speed went from 10M-41M maximum tries per second with the slider setting now to about 530M on an RTX 4090 in Zen.

<a href="https://ko-fi.com/koifist"><img alt="Ko-fi" src="https://img.shields.io/badge/Ko--fi-support-eb6f92?logo=kofi&logoColor=e0def4&style=for-the-badge&labelColor=232136" /></a>

<table>
  <tr>
    <th>Before</th>
    <th>After</th>
  </tr>
  <tr>
    <td valign="top"><img width="420" alt="Before" src="https://github.com/user-attachments/assets/4387ce97-7549-4b18-94dc-bee7257b10cd" /></td>
    <td valign="top"><img width="420" alt="After" src="https://github.com/user-attachments/assets/2b9f27a8-8ca1-4bdb-ab47-f65712f30087" /></td>
  </tr>
</table>
