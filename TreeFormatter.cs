using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace TreeDump
{
    public static class TreeFormatter
    {
        public static string GenerateAsciiTree(FileNode? root, bool onlyChecked = false)
        {
            if (root == null)
                return string.Empty;

            var sb = new StringBuilder(2048);
            string rootSuffix = root.IsDirectory ? "/" : string.Empty;
            sb.Append(root.Name).AppendLine(rootSuffix);

            if (!root.IsDirectory)
                return sb.ToString();

            List<FileNode> children = onlyChecked
                ? root.Children.Where(HasCheckedDescendant).ToList()
                : root.Children.ToList();

            for (int i = 0; i < children.Count; i++)
            {
                bool isLast = i == children.Count - 1;
                if (onlyChecked)
                {
                    BuildExportTree(children[i], string.Empty, isLast, sb);
                }
                else
                {
                    BuildTree(children[i], string.Empty, isLast, sb);
                }
            }

            return sb.ToString();
        }

        public static void BuildTree(FileNode node, string indent, bool isLast, StringBuilder sb)
        {
            if (node == null || sb == null)
                return;

            string marker = isLast ? "└── " : "├── ";
            string suffix = node.IsDirectory ? "/" : string.Empty;
            sb.Append(indent).Append(marker).Append(node.Name).AppendLine(suffix);

            if (!node.IsDirectory || node.Children.Count == 0)
                return;

            string nextIndent = indent + (isLast ? "    " : "│   ");
            for (int i = 0; i < node.Children.Count; i++)
            {
                BuildTree(node.Children[i], nextIndent, i == node.Children.Count - 1, sb);
            }
        }

        public static void BuildExportTree(FileNode node, string indent, bool isLast, StringBuilder sb)
        {
            if (node == null || sb == null || !HasCheckedDescendant(node))
                return;

            string marker = isLast ? "└── " : "├── ";
            string suffix = node.IsDirectory ? "/" : string.Empty;
            sb.Append(indent).Append(marker).Append(node.Name).AppendLine(suffix);

            if (!node.IsDirectory)
                return;

            var checkedChildren = node.Children.Where(HasCheckedDescendant).ToList();
            string nextIndent = indent + (isLast ? "    " : "│   ");
            for (int i = 0; i < checkedChildren.Count; i++)
            {
                BuildExportTree(checkedChildren[i], nextIndent, i == checkedChildren.Count - 1, sb);
            }
        }

        private static bool HasCheckedDescendant(FileNode node)
        {
            if (!node.IsDirectory)
                return node.IsChecked;

            foreach (var child in node.Children)
            {
                if (HasCheckedDescendant(child))
                    return true;
            }

            return false;
        }
    }
}