# Native third-party components

The bundled fastText language-identification model `lid.176.bin` is an unchanged
copy of the Facebook fastText model. It was trained on Wikipedia, Tatoeba and
SETimes data. The model is distributed under Creative Commons Attribution-Share
Alike 3.0; this license applies to the model, which is retained as a separate file.

- Model and attribution: https://fasttext.cc/docs/en/language-identification.html
- Original model: https://dl.fbaipublicfiles.com/fasttext/supervised-models/lid.176.bin
- License: https://creativecommons.org/licenses/by-sa/3.0/
- Legal code: https://creativecommons.org/licenses/by-sa/3.0/legalcode
- Bytes: `131266198`
- SHA-256: `7e69ec5451bc261cc7844e49e4792a85d7f09c06789ec800fc4a44aec362764e`
- Model modifications: none.

References requested by fastText: Armand Joulin, Edouard Grave, Piotr Bojanowski,
Tomas Mikolov, *Bag of Tricks for Efficient Text Classification* (2016),
https://arxiv.org/abs/1607.01759; Armand Joulin, Edouard Grave, Piotr Bojanowski,
Matthijs Douze, Hérve Jégou, Tomas Mikolov, *FastText.zip: Compressing text
classification models* (2016), https://arxiv.org/abs/1612.03651.

The fastText engine is compiled from commit
`1142dc4c4ecbc19cc16eee5cdd28472e689267e6`. Its upstream MIT license is included as
`ThirdPartyLicenses/fastText-LICENSE.txt`. DropSpace's pipe adapter is separate
from the unchanged engine and validates model bytes before loading them.

The llama.cpp engine is compiled from commit
`7fe450e19305b828c199d602c23a8337aaa1f03b`. Its MIT license is included in packaged
candidates as `ThirdPartyLicenses/llama.cpp-LICENSE.txt`. Translation models are
separate optional components; their license and attribution must accompany each
component. A compiled engine does not establish model inference acceptance.
