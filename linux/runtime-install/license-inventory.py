"""Print installed dependency names/licenses only; never inspect credentials or environment."""
import importlib.metadata
import hashlib
import json
items=[]
for distribution in importlib.metadata.distributions():
    metadata=distribution.metadata
    licenses=[]
    for file in distribution.files or []:
        name=str(file)
        if '.dist-info/' in name and any(word in name.lower() for word in ('license','copying','notice')):
            path=distribution.locate_file(file)
            if path.is_file(): licenses.append({'path':name,'sha256':hashlib.sha256(path.read_bytes()).hexdigest()})
    items.append({'name':metadata.get('Name'),'version':distribution.version,'licenseExpression':metadata.get('License-Expression'),
        'license':metadata.get('License'),'classifiers':[value for value in metadata.get_all('Classifier',[]) if value.startswith('License ::')],'licenseFiles':licenses})
print(json.dumps(sorted(items,key=lambda item:item['name'].lower()),indent=2))
